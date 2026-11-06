using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace GokouKotori.FBXUVTextureTransfer
{
    // CPU reference for tests. It follows BodyOverlayTransfer.shader and PixelProcess.compute step by step: the same
    // map encoding, nearest texel, island-aware upsampling, masked premultiplied bilinear sampling,
    // D3D11 rasterization of the island mask, jump-flood fill and 8-connected bleed. Deliberate difference:
    // the overlay is read as stored bytes. The GPU decodes an sRGB overlay and re-encodes it with
    // GammaFromLinear, which returns the same bytes up to float rounding. Assumes an 8-bit RGBA canvas.
    // Every multiply-add the shader compiles to mad (lerp, source * size - 0.5, the upsampling sums) is fused on the
    // GPU (measured on D3D11: bit-equal alpha), so the reference fuses them too; the shader then rounds each channel
    // to k/255 itself (Quantize), which the reference repeats in Unorm. Both are needed so a pixel whose alpha lies
    // near 0.5/255 is stored the same way on both sides, because that decides what the bleed copies.
    internal static class FBXUVBodyOverlayCpuReference
    {
        internal static Color32[] Render(FBXUVBodyCorrespondence correspondence, Texture2D readableOverlay, int width, int height)
        {
            if (correspondence == null) throw new ArgumentNullException(nameof(correspondence));
            if (readableOverlay == null) throw new ArgumentNullException(nameof(readableOverlay));
            if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
            var resolution = correspondence.Resolution;
            var map = FBXUVBodyOverlayGpuResources.BuildMap(correspondence, new RectInt(0, 0, resolution, resolution));
            var islands = FBXUVBodyOverlayGpuResources.BuildIslands(correspondence);
            var overlay = new CpuOverlay(new CpuSourceMask(FBXUVBodyOverlayGpuResources.BuildSourceLookup(correspondence.Source)), readableOverlay);
            var mask = RasterizeTargetMask(correspondence, width, height);
            var covered = new bool[width * height];
            var color = new Color32[width * height];
            Parallel.For(0, height, y =>
            {
                for (var x = 0; x < width; x++)
                {
                    var i = y * width + x;
                    int tx = Nearest(x, width, resolution), ty = Nearest(y, height, resolution);
                    covered[i] = islands[ty * resolution + tx] >= 0f;
                    color[i] = Quantize(Transfer(x, y, tx, ty, width, height, resolution, map, islands, overlay));
                }
            });
            return FillAndBleed(color, mask, covered, width, height);
        }

        // Island mask as the D3D11 rasterizer draws the target mesh: the vertex shader's float uv * 2 - 1,
        // the viewport with row 0 at v = 0, vertices snapped to 1/256 px (round to nearest even), pixel centers
        // and the top-left rule. Exact for power-of-two canvases.
        internal static bool[] RasterizeTargetMask(FBXUVBodyCorrespondence correspondence, int width, int height)
        {
            var target = correspondence.Target;
            var points = new long[target.Uvs.Length * 2];
            for (var i = 0; i < target.Uvs.Length; i++)
            {
                points[i * 2] = Snap(target.Uvs[i].x, width);
                points[i * 2 + 1] = Snap(target.Uvs[i].y, height);
            }
            var mask = new bool[width * height];
            var triangles = target.Triangles;
            for (var t = 0; t + 2 < triangles.Length; t += 3)
            {
                RasterizeTriangle(points[triangles[t] * 2], points[triangles[t] * 2 + 1],
                    points[triangles[t + 1] * 2], points[triangles[t + 1] * 2 + 1],
                    points[triangles[t + 2] * 2], points[triangles[t + 2] * 2 + 1], mask, width, height);
            }
            return mask;
        }

        private static long Snap(float uv, int size)
        {
            var ndc = (float)(uv * 2f - 1f);
            return (long)Math.Round(((double)ndc + 1.0) * 0.5 * size * 256.0, MidpointRounding.ToEven);
        }

        private static void RasterizeTriangle(long ax, long ay, long bx, long by, long cx, long cy, bool[] mask, int width, int height)
        {
            var area = (bx - ax) * (cy - ay) - (by - ay) * (cx - ax);
            if (area == 0) return;
            if (area < 0)
            {
                (bx, cx) = (cx, bx);
                (by, cy) = (cy, by);
            }
            var minX = Math.Max(0, (int)Math.Ceiling((Math.Min(ax, Math.Min(bx, cx)) - 128) / 256.0));
            var maxX = Math.Min(width - 1, (int)Math.Floor((Math.Max(ax, Math.Max(bx, cx)) - 128) / 256.0));
            var minY = Math.Max(0, (int)Math.Ceiling((Math.Min(ay, Math.Min(by, cy)) - 128) / 256.0));
            var maxY = Math.Min(height - 1, (int)Math.Floor((Math.Max(ay, Math.Max(by, cy)) - 128) / 256.0));
            for (var y = minY; y <= maxY; y++)
            for (var x = minX; x <= maxX; x++)
            {
                long px = x * 256L + 128, py = y * 256L + 128;
                if (Covers(bx, by, cx, cy, px, py) && Covers(cx, cy, ax, ay, px, py) && Covers(ax, ay, bx, by, px, py))
                    mask[y * width + x] = true;
            }
        }

        // Edge a -> b of a triangle with positive area in y-down pixels; a center on the edge counts only for
        // top (horizontal, rightward) and left (upward) edges.
        private static bool Covers(long ax, long ay, long bx, long by, long px, long py)
        {
            long dx = bx - ax, dy = by - ay;
            var edge = dx * (py - ay) - dy * (px - ax);
            return edge > 0 || (edge == 0 && (dy < 0 || (dy == 0 && dx > 0)));
        }

        // Integer form of floor((pixel + 0.5) * R / size), as NearestTexel in the shader.
        private static int Nearest(int pixel, int size, int resolution)
        {
            return (int)Math.Min((2L * pixel + 1) * resolution / (2L * size), resolution - 1);
        }

        private static Vector4 Transfer(int x, int y, int tx, int ty, int width, int height, int resolution,
            float[] map, float[] islands, CpuOverlay overlay)
        {
            if (!TryLoad(map, islands, resolution, tx, ty, out var uv, out var island)) return Vector4.zero;
            if (width > resolution || height > resolution)
            {
                var tX = Fma(x + .5f, (float)resolution / width, -.5f);
                var tY = Fma(y + .5f, (float)resolution / height, -.5f);
                var baseX = Mathf.Floor(tX);
                var baseY = Mathf.Floor(tY);
                float fx = tX - baseX, fy = tY - baseY;
                int ox = (int)baseX, oy = (int)baseY;
                if (width <= resolution) { ox = tx; fx = 0f; }
                if (height <= resolution) { oy = ty; fy = 0f; }
                // Interpolate only among the qualifying neighbours, with their bilinear weights renormalised.
                var limit = FBXUVBodyOverlayTransferRenderer.SameSourceIslandPixels / overlay.Width;
                var sum = Vector2.zero;
                var total = Qualify(map, islands, resolution, ox, oy, (1f - fx) * (1f - fy), island, uv, limit, ref sum);
                total += Qualify(map, islands, resolution, ox + 1, oy, fx * (1f - fy), island, uv, limit, ref sum);
                total += Qualify(map, islands, resolution, ox, oy + 1, (1f - fx) * fy, island, uv, limit, ref sum);
                total += Qualify(map, islands, resolution, ox + 1, oy + 1, fx * fy, island, uv, limit, ref sum);
                if (total > 0f) uv = new Vector2(sum.x / total, sum.y / total);
            }
            return overlay.Sample(uv);
        }

        private static bool TryLoad(float[] map, float[] islands, int resolution, int x, int y, out Vector2 uv, out float island)
        {
            uv = new Vector2(-1f, -1f);
            island = -1f;
            if (x < 0 || y < 0 || x >= resolution || y >= resolution) return false;
            var index = y * resolution + x;
            island = islands[index];
            uv = new Vector2(map[index * 2], map[index * 2 + 1]);
            return island >= 0f && uv.x > -.5f;
        }

        private static float Qualify(float[] map, float[] islands, int resolution, int x, int y, float weight,
            float island, Vector2 nearest, float limit, ref Vector2 sum)
        {
            if (weight <= 0f) return 0f;
            if (!TryLoad(map, islands, resolution, x, y, out var uv, out var candidate) || candidate != island) return 0f;
            var dx = uv.x - nearest.x;
            var dy = uv.y - nearest.y;
            if (dx * dx + dy * dy >= limit * limit) return 0f;
            sum = new Vector2(Fma(weight, uv.x, sum.x), Fma(weight, uv.y, sum.y));
            return weight;
        }

        private static Color32 Quantize(Vector4 color)
        {
            return new Color32(Unorm(color.x), Unorm(color.y), Unorm(color.z), Unorm(color.w));
        }

        // Quantize in the shader: round(saturate(c) * 255) with HLSL round (round half to even), stored exactly as k.
        private static byte Unorm(float value)
        {
            return (byte)Math.Round((float)(Mathf.Clamp01(value) * 255f), MidpointRounding.ToEven);
        }

        // a * b + c rounded once, like the GPU's fused mad. The double product of two floats is exact.
        private static float Fma(float a, float b, float c)
        {
            return (float)((double)a * b + c);
        }

        // InitializeSeeds/PropagateSeeds/ResolveFill, then Dilate8Connected, as in PixelProcess.compute.
        private static Color32[] FillAndBleed(Color32[] source, bool[] mask, bool[] covered, int width, int height)
        {
            var count = width * height;
            var read = new int[count];
            var write = new int[count];
            for (var i = 0; i < count; i++) read[i] = mask[i] && covered[i] ? i : -1;
            for (var jump = FBXUVPixelProcessor.HighestJump(width, height); jump >= 1; jump >>= 1)
            {
                var step = jump;
                var from = read;
                var to = write;
                Parallel.For(0, height, y =>
                {
                    for (var x = 0; x < width; x++)
                    {
                        var best = from[y * width + x];
                        var bestDistance = best >= 0 ? Distance(best, x, y, width) : long.MaxValue;
                        for (var oy = -1; oy <= 1; oy++)
                        for (var ox = -1; ox <= 1; ox++)
                        {
                            if (ox == 0 && oy == 0) continue;
                            int nx = x + ox * step, ny = y + oy * step;
                            if (nx < 0 || ny < 0 || nx >= width || ny >= height) continue;
                            var candidate = from[ny * width + nx];
                            if (candidate < 0) continue;
                            var distance = Distance(candidate, x, y, width);
                            if (distance >= bestDistance) continue;
                            bestDistance = distance;
                            best = candidate;
                        }
                        to[y * width + x] = best;
                    }
                });
                read = to;
                write = from;
            }

            var current = new Color32[count];
            for (var i = 0; i < count; i++)
            {
                if (!mask[i]) continue;
                if (covered[i]) current[i] = source[i];
                else if (read[i] >= 0) current[i] = source[read[i]];
            }

            var other = new Color32[count];
            for (var pass = 0; pass < FBXUVBodyOverlayTransferRenderer.BleedPixels; pass++)
            {
                var input = current;
                var output = other;
                Parallel.For(0, height, y =>
                {
                    for (var x = 0; x < width; x++)
                    {
                        var i = y * width + x;
                        if (mask[i] || input[i].a > 0)
                        {
                            output[i] = input[i];
                            continue;
                        }
                        var replacement = default(Color32);
                        for (var oy = -1; oy <= 1 && replacement.a == 0; oy++)
                        for (var ox = -1; ox <= 1; ox++)
                        {
                            int nx = x + ox, ny = y + oy;
                            if ((ox == 0 && oy == 0) || nx < 0 || ny < 0 || nx >= width || ny >= height) continue;
                            var candidate = input[ny * width + nx];
                            if (candidate.a == 0) continue;
                            replacement = candidate;
                            break;
                        }
                        output[i] = replacement;
                    }
                });
                current = output;
                other = input;
            }
            return current;
        }

        private static long Distance(int seed, int x, int y, int width)
        {
            long dx = seed % width - x, dy = seed / width - y;
            return dx * dx + dy * dy;
        }

        // Premultiplied overlay texels with the source triangle mask applied, as LoadPremultiplied.
        private sealed class CpuOverlay
        {
            internal readonly int Width;
            private readonly int height;
            private readonly Vector4[] premultiplied;

            internal CpuOverlay(CpuSourceMask mask, Texture2D texture)
            {
                Width = texture.width;
                height = texture.height;
                var pixels = texture.GetPixels32();
                premultiplied = new Vector4[pixels.Length];
                Parallel.For(0, height, y =>
                {
                    for (var x = 0; x < Width; x++)
                    {
                        var uv = new Vector2((x + .5f) / Width, (y + .5f) / height);
                        if (!mask.Contains(uv)) continue;
                        var p = pixels[y * Width + x];
                        var a = p.a / 255f;
                        premultiplied[y * Width + x] = new Vector4(p.r / 255f * a, p.g / 255f * a, p.b / 255f * a, a);
                    }
                });
            }

            internal Vector4 Sample(Vector2 source)
            {
                var px = Fma(source.x, Width, -.5f);
                var py = Fma(source.y, height, -.5f);
                var floorX = Mathf.Floor(px);
                var floorY = Mathf.Floor(py);
                int ox = (int)floorX, oy = (int)floorY;
                float wx = px - floorX, wy = py - floorY;
                var lower = Lerp(Load(ox, oy), Load(ox + 1, oy), wx);
                var upper = Lerp(Load(ox, oy + 1), Load(ox + 1, oy + 1), wx);
                var color = Lerp(lower, upper, wy);
                return color.w > 0f ? new Vector4(color.x / color.w, color.y / color.w, color.z / color.w, color.w) : Vector4.zero;
            }

            // HLSL lerp(a, b, t) compiles to add + mad: a + t * (b - a) with the multiply-add fused.
            private static Vector4 Lerp(Vector4 a, Vector4 b, float t)
            {
                return new Vector4(Lerp(a.x, b.x, t), Lerp(a.y, b.y, t), Lerp(a.z, b.z, t), Lerp(a.w, b.w, t));
            }

            private static float Lerp(float a, float b, float t)
            {
                return Fma(t, (float)(b - a), a);
            }

            private Vector4 Load(int x, int y)
            {
                return x < 0 || y < 0 || x >= Width || y >= height ? Vector4.zero : premultiplied[y * Width + x];
            }
        }

        // The containment test of FBXUVSourceSampling.hlsl over the records and grid of FBXUVSourceTriangleMask.
        private sealed class CpuSourceMask
        {
            private readonly int gridSize;
            private readonly List<Vector4> records;
            private readonly Vector2Int[] ranges;
            private readonly int[] indices;

            internal CpuSourceMask(FBXUVSourceTriangleMask.Lookup lookup)
            {
                gridSize = lookup.GridSize;
                records = lookup.Triangles;
                ranges = lookup.CellRanges;
                indices = lookup.CellIndices;
            }

            internal bool Contains(Vector2 uv)
            {
                if (uv.x < 0f || uv.y < 0f || uv.x > 1f || uv.y > 1f) return false;
                int cellX = Math.Min((int)(uv.x * gridSize), gridSize - 1), cellY = Math.Min((int)(uv.y * gridSize), gridSize - 1);
                var range = ranges[cellY * gridSize + cellX];
                for (var i = 0; i < range.y; i++)
                {
                    var index = indices[range.x + i] * 2;
                    var ab = records[index];
                    var c = records[index + 1];
                    float acX = ab.x - c.x, acY = ab.y - c.y, bcX = ab.z - c.x, bcY = ab.w - c.y;
                    float pcX = uv.x - c.x, pcY = uv.y - c.y, abX = ab.x - ab.z, abY = ab.y - ab.w;
                    var denominator = acX * bcY - acY * bcX;
                    var edgeSquared = Math.Max(acX * acX + acY * acY, Math.Max(bcX * bcX + bcY * bcY, abX * abX + abY * abY));
                    if (Math.Abs(denominator) <= edgeSquared * 1e-12f) continue;
                    var alpha = (pcX * bcY - pcY * bcX) / denominator;
                    var beta = (acX * pcY - acY * pcX) / denominator;
                    if (alpha >= -1e-6f && beta >= -1e-6f && 1f - alpha - beta >= -1e-6f) return true;
                }
                return false;
            }
        }
    }
}
