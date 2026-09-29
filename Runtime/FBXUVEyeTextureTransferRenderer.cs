using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace GokouKotori.FBXUVTextureTransfer
{
    internal static class FBXUVEyeTextureTransferRenderer
    {
        internal static void Render(FBXUVEyeTextureTransferLayer layer, RenderTexture destination, IReadOnlyList<FBXUVEyeMapping> mappings)
        {
            var previous = RenderTexture.active; var srgb = GL.sRGBWrite;
            try
            {
                using (var resources = new FBXUVRenderResources(layer.ResolveEyeShader(), layer.ResolvePixelProcessShader()))
                {
                    var material = resources.Material;
                    material.SetTexture("_MainTex", layer.sourceTexture);
                    material.SetFloat("_RestoreSRGB", GraphicsFormatUtility.IsSRGBFormat(layer.sourceTexture.graphicsFormat) ? 1 : 0);
                    foreach (var mapping in mappings)
                    {
                        using (var iris = new Surface(mapping.Iris, mapping.SourceFrame, mapping.IrisCoordinates))
                        using (var pupil = mapping.HasPupil ? new Surface(mapping.Pupil, mapping.SourceFrame) : null)
                        using (var occlusion = mapping.HasPupil && mapping.IrisCoordinates != null ? new Surface(mapping.Iris, mapping.SourceFrame) : null)
                        using (var target = mapping.ManualWarp != null ? new Surface(mapping.Target, mapping.TargetFrame, null,
                            mapping.TargetCoordinates?.Select(p => (Vector2)p).ToArray()
                            ?? mapping.Target.Positions.Select(p => (Vector2)mapping.TargetFrame.Project(p)).ToArray()) : null)
                        using (var manual = Buffer(mapping.ManualWarp?.Data ?? Array.Empty<Vector4>(), 16))
                        using (var sourceContour = Buffer(mapping.SourceFrame.Contour.Select(v => new Vector4(v.x, v.y, 0, 0)).ToArray(), 16))
                        using (var targetContour = Buffer(mapping.TargetFrame.Contour.Select(v => new Vector4(v.x, v.y, 0, 0)).ToArray(), 16))
                        {
                            iris.Bind(material, "_Iris"); (pupil ?? iris).Bind(material, "_Pupil");
                            (occlusion ?? iris).Bind(material, "_Occlusion");
                            (target ?? iris).Bind(material, "_Target");
                            material.SetBuffer("_ManualPins", manual);
                            material.SetInt("_ManualCount", (mapping.ManualWarp?.Data.Length ?? 0) / 2);
                            material.SetInt("_HasPupil", mapping.HasPupil ? 1 : 0);
                            material.SetBuffer("_SourceContour", sourceContour); material.SetInt("_SourceCount", sourceContour.count);
                            material.SetBuffer("_TargetContour", targetContour); material.SetInt("_TargetCount", targetContour.count);
                            material.SetVector("_PupilCenters", new Vector4(mapping.SourcePupilCenter.x, mapping.SourcePupilCenter.y, mapping.TargetPupilCenter.x, mapping.TargetPupilCenter.y));
                            material.SetVector("_PupilScale", mapping.PupilScale);
                            var bounds = FBXUVBounds.FromTriangles(ToUvTriangles(mapping.Target));
                            var pixels = FBXUVTextureTransferRenderer.GetExpandedPixelBounds(bounds, destination.width, destination.height, 0);
                            var scratch = FBXUVTextureTransferRenderer.GetColorTemporary(destination, pixels.Width, pixels.Height, "FBXUV Eye");
                            try
                            {
                                FBXUVTextureTransferRenderer.Clear(scratch);
                                var mesh = resources.TransferMesh;
                                var positions = mapping.Target.Uvs.Select(uv => new Vector3(uv.x * destination.width - pixels.Left,
                                    (1 - uv.y) * destination.height - pixels.Top, 0)).ToArray();
                                mesh.Clear(); mesh.indexFormat = positions.Length > ushort.MaxValue ? IndexFormat.UInt32 : IndexFormat.UInt16;
                                mesh.vertices = positions;
                                var physical = mapping.Target.Positions.Select(p => (Vector2)mapping.TargetFrame.Project(p)).ToArray();
                                mesh.uv = mapping.TargetCoordinates?.Select(p => (Vector2)p).ToArray() ?? physical;
                                mesh.uv2 = physical;
                                mesh.triangles = mapping.Target.Triangles;
                                material.SetVector("_OutputSize", new Vector4(scratch.width, scratch.height, 0, 0));
                                Graphics.SetRenderTarget(scratch); GL.sRGBWrite = false;
                                if (!material.SetPass(0)) throw new InvalidOperationException("Eye転送Shaderの描画passを利用できません。");
                                Graphics.DrawMeshNow(mesh, Matrix4x4.identity);
                                // Use the existing straight-alpha compositor and its TTT row convention.
                                resources.Pixels.CompositeStraightAlpha(scratch, destination, pixels.Left, pixels.Top);
                            }
                            finally { FBXUVTextureTransferRenderer.ReleaseTemporary(scratch); }
                        }
                    }
                }
            }
            finally { GL.sRGBWrite = srgb; Graphics.SetRenderTarget(previous); }
        }

        private static List<FBXUVTriangle> ToUvTriangles(FBXUVEyeGeometry geometry)
        {
            var result = new List<FBXUVTriangle>();
            for (var i = 0; i < geometry.Uvs.Length; i += 3)
                result.Add(new FBXUVTriangle(i / 3, geometry.Uvs[i], geometry.Uvs[i + 1], geometry.Uvs[i + 2]));
            return result;
        }

        private static ComputeBuffer Buffer<T>(T[] data, int stride) where T : struct
        {
            var buffer = new ComputeBuffer(Math.Max(1, data.Length), stride);
            try { if (data.Length > 0) buffer.SetData(data); return buffer; }
            catch { buffer.Dispose(); throw; }
        }

        // Grid lookup is in the fitted eye plane, independent of how the artist packed the UVs.
        private sealed class Surface : IDisposable
        {
            private const int Grid = 32;
            private ComputeBuffer triangles, ranges, indices;
            private Vector4 bounds;
            internal Surface(FBXUVEyeGeometry geometry, FBXUVEyeFrame frame, Vector3[] coordinates = null, Vector2[] values = null)
            {
                try
                {
                    var p = coordinates ?? geometry.Positions.Select(frame.Project).ToArray();
                    var min = p.Select(v => (Vector2)v).Aggregate(Vector2.Min) - Vector2.one * 1e-5f;
                    var max = p.Select(v => (Vector2)v).Aggregate(Vector2.Max) + Vector2.one * 1e-5f;
                    bounds = new Vector4(min.x, min.y, 1 / (max.x - min.x), 1 / (max.y - min.y));
                    int Cell(float value, bool x) => Mathf.Clamp((int)((value - (x ? min.x : min.y)) * (x ? bounds.z : bounds.w) * Grid), 0, Grid - 1);
                    var cells = new List<int>[Grid * Grid]; var data = new List<Vector4>();
                    for (var i = 0; i < p.Length; i += 3)
                    {
                        if (Mathf.Abs(FBXUVEyeFrame.Cross((Vector2)(p[i + 1] - p[i]), (Vector2)(p[i + 2] - p[i]))) < 1e-10f)
                            throw new ArgumentException("目の投影に退化した三角形があります。");
                        data.Add(p[i]); data.Add(p[i + 1]); data.Add(p[i + 2]);
                        var uv = values ?? geometry.Uvs;
                        data.Add(new Vector4(uv[i].x, uv[i].y, uv[i + 1].x, uv[i + 1].y)); data.Add(uv[i + 2]);
                        var lo = Vector3.Min(p[i], Vector3.Min(p[i + 1], p[i + 2]));
                        var hi = Vector3.Max(p[i], Vector3.Max(p[i + 1], p[i + 2]));
                        for (var y = Cell(lo.y - 1e-5f, false); y <= Cell(hi.y + 1e-5f, false); y++)
                            for (var x = Cell(lo.x - 1e-5f, true); x <= Cell(hi.x + 1e-5f, true); x++)
                            { var cell = y * Grid + x; (cells[cell] ??= new List<int>()).Add(i / 3); }
                    }
                    var lookup = new Vector2Int[cells.Length]; var ids = new List<int>();
                    for (var i = 0; i < cells.Length; i++)
                    { lookup[i] = new Vector2Int(ids.Count, cells[i]?.Count ?? 0); if (cells[i] != null) ids.AddRange(cells[i]); }
                    triangles = Buffer(data.ToArray(), 16); ranges = Buffer(lookup, 8); indices = Buffer(ids.ToArray(), 4);
                }
                catch { Dispose(); throw; }
            }
            internal void Bind(Material material, string prefix)
            {
                material.SetBuffer(prefix + "Triangles", triangles); material.SetBuffer(prefix + "Ranges", ranges);
                material.SetBuffer(prefix + "Indices", indices); material.SetVector(prefix + "Bounds", bounds);
            }
            public void Dispose() { triangles?.Dispose(); ranges?.Dispose(); indices?.Dispose(); }
        }
    }
}
