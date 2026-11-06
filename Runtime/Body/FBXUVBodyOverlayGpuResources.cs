using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace GokouKotori.FBXUVTextureTransfer
{
    // The GPU half of one correspondence cache entry (spec §4.6). Created on the first render and stored in
    // FBXUVBodyCorrespondenceEntry.Gpu, so LoadImage does not upload the map again; the cache disposes it on eviction.
    internal sealed class FBXUVBodyOverlayGpuResources : IDisposable
    {
        internal const int SourceMaskGridSize = 128;
        internal const float InvalidUv = -1f;
        private const string EmptySourceMessage = "転送元のボディ三角形がありません。";
        private const string InvalidSourceUvMessage = "転送元のボディ三角形には0〜1の有限UVが必要です。";
        private const string DegenerateSourceMessage = "転送元のボディ三角形が全て退化しています。";

        private FBXUVBodyOverlayGpuResources()
        {
        }

        internal int Resolution { get; private set; }
        // RGFloat source UV per texel; (-1, -1) where the texel is invalid or not covered.
        internal Texture2D Map { get; private set; }
        // RFloat target island per texel; -1 where the texel is not covered.
        internal Texture2D Islands { get; private set; }
        internal FBXUVSourceTriangleMask SourceMask { get; private set; }
        // The full-canvas square drawn by the transfer and coverage passes, in UV 0..1.
        internal Mesh Quad { get; private set; }
        // The target body triangles in UV, drawn as the island mask for FillAndBleed.
        internal Mesh TargetMesh { get; private set; }

        internal bool IsAlive => Map != null && Islands != null && Quad != null && TargetMesh != null && SourceMask != null;

        // The entry's resources, created on first use. An entry the cache has dropped already (held across an eviction, ruling
        // T12-4) gets temporary resources instead, not attached to it (temporary = true): the caller disposes them after use.
        internal static FBXUVBodyOverlayGpuResources Get(FBXUVBodyCorrespondenceEntry entry, out bool temporary)
        {
            temporary = entry.IsDisposed;
            if (temporary) return Create(entry.Correspondence);
            if (entry.Gpu is FBXUVBodyOverlayGpuResources current && current.IsAlive) return current;
            var stale = entry.Gpu;
            entry.Gpu = null;
            stale?.Dispose();
            var created = Create(entry.Correspondence);
            entry.Gpu = created;
            return created;
        }

        private static FBXUVBodyOverlayGpuResources Create(FBXUVBodyCorrespondence correspondence)
        {
            var resources = new FBXUVBodyOverlayGpuResources { Resolution = correspondence.Resolution };
            try
            {
                resources.SourceMask = FBXUVSourceTriangleMask.Create(SourceTriangles(correspondence.Source), SourceMaskGridSize,
                    EmptySourceMessage, InvalidSourceUvMessage, DegenerateSourceMessage);
                var all = new RectInt(0, 0, correspondence.Resolution, correspondence.Resolution);
                resources.Map = CreateMapTexture(correspondence.Resolution, correspondence.Resolution, TextureFormat.RGFloat, "FBXUV Body Correspondence");
                resources.Map.SetPixelData(BuildMap(correspondence, all), 0);
                resources.Map.Apply(false, true);
                resources.Islands = CreateMapTexture(correspondence.Resolution, correspondence.Resolution, TextureFormat.RFloat, "FBXUV Body Islands");
                resources.Islands.SetPixelData(BuildIslands(correspondence), 0);
                resources.Islands.Apply(false, true);
                resources.Quad = CreateQuad();
                resources.TargetMesh = CreateTargetMesh(correspondence.Target);
                return resources;
            }
            catch
            {
                resources.Dispose();
                throw;
            }
        }

        internal void Bind(Material material)
        {
            material.SetTexture("_CorrespondenceMap", Map);
            material.SetTexture("_IslandMap", Islands);
            material.SetInt("_MapResolution", Resolution);
            SourceMask.Bind(material);
        }

        // Uploads only the given texel rectangle of the current SourceUv/Valid (pins rewrite small regions).
        internal void Upload(FBXUVBodyCorrespondence correspondence, RectInt texels)
        {
            var xMin = Mathf.Max(0, texels.xMin);
            var yMin = Mathf.Max(0, texels.yMin);
            var xMax = Mathf.Min(Resolution, texels.xMax);
            var yMax = Mathf.Min(Resolution, texels.yMax);
            if (xMax <= xMin || yMax <= yMin) return;
            var rect = new RectInt(xMin, yMin, xMax - xMin, yMax - yMin);
            if (SystemInfo.copyTextureSupport == CopyTextureSupport.None)
            {
                var replacement = CreateMapTexture(Resolution, Resolution, TextureFormat.RGFloat, "FBXUV Body Correspondence");
                try
                {
                    replacement.SetPixelData(BuildMap(correspondence, new RectInt(0, 0, Resolution, Resolution)), 0);
                    replacement.Apply(false, true);
                    UnityEngine.Object.DestroyImmediate(Map);
                    Map = replacement;
                    replacement = null;
                }
                finally
                {
                    if (replacement != null) UnityEngine.Object.DestroyImmediate(replacement);
                }
                return;
            }

            var staging = CreateMapTexture(rect.width, rect.height, TextureFormat.RGFloat, "FBXUV Body Correspondence Staging");
            try
            {
                staging.SetPixelData(BuildMap(correspondence, rect), 0);
                staging.Apply(false, false);
                Graphics.CopyTexture(staging, 0, 0, 0, 0, rect.width, rect.height, Map, 0, 0, rect.x, rect.y);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(staging);
            }
        }

        // Rows of the rectangle, row 0 = texel row rect.y; two floats per texel.
        internal static float[] BuildMap(FBXUVBodyCorrespondence correspondence, RectInt rect)
        {
            var resolution = correspondence.Resolution;
            var data = new float[rect.width * rect.height * 2];
            for (var i = 0; i < data.Length; i++) data[i] = InvalidUv;
            var texels = correspondence.Texels;
            for (var row = 0; row < rect.height; row++)
            {
                var first = (rect.y + row) * resolution + rect.x;
                var end = first + rect.width;
                var index = Array.BinarySearch(texels, first);
                if (index < 0) index = ~index;
                for (; index < texels.Length && texels[index] < end; index++)
                {
                    if (!correspondence.Valid[index]) continue;
                    var offset = (row * rect.width + texels[index] - first) * 2;
                    data[offset] = correspondence.SourceUv[index].x;
                    data[offset + 1] = correspondence.SourceUv[index].y;
                }
            }
            return data;
        }

        internal static float[] BuildIslands(FBXUVBodyCorrespondence correspondence)
        {
            var data = new float[correspondence.Resolution * correspondence.Resolution];
            for (var i = 0; i < data.Length; i++) data[i] = -1f;
            for (var i = 0; i < correspondence.Texels.Length; i++) data[correspondence.Texels[i]] = correspondence.TargetIsland[i];
            return data;
        }

        // The CPU form of SourceMask, for the CPU reference.
        internal static FBXUVSourceTriangleMask.Lookup BuildSourceLookup(FBXUVBodyGeometry source)
        {
            return FBXUVSourceTriangleMask.BuildLookup(SourceTriangles(source), SourceMaskGridSize,
                EmptySourceMessage, InvalidSourceUvMessage, DegenerateSourceMessage);
        }

        // Every source body triangle inside the 0..1 tile; triangles outside it cannot be read from the overlay.
        internal static List<FBXUVTriangle> SourceTriangles(FBXUVBodyGeometry source)
        {
            var count = source.Triangles.Length / 3;
            var result = new List<FBXUVTriangle>(count);
            for (var t = 0; t < count; t++)
            {
                var a = source.Uvs[source.Triangles[t * 3]];
                var b = source.Uvs[source.Triangles[t * 3 + 1]];
                var c = source.Uvs[source.Triangles[t * 3 + 2]];
                if (!FBXUVMakeupWarp.IsUnitUv(a) || !FBXUVMakeupWarp.IsUnitUv(b) || !FBXUVMakeupWarp.IsUnitUv(c)) continue;
                result.Add(new FBXUVTriangle(t, a, b, c));
            }
            return result;
        }

        private static Texture2D CreateMapTexture(int width, int height, TextureFormat format, string name)
        {
            return new Texture2D(width, height, format, false, true)
            {
                name = name,
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };
        }

        private static Mesh CreateQuad()
        {
            var mesh = new Mesh { name = "FBXUV Body Canvas Quad", hideFlags = HideFlags.HideAndDontSave };
            mesh.SetVertices(new List<Vector3> { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(1, 1, 0), new Vector3(0, 1, 0) });
            mesh.SetIndices(new[] { 0, 1, 2, 0, 2, 3 }, MeshTopology.Triangles, 0, true);
            mesh.UploadMeshData(true);
            return mesh;
        }

        private static Mesh CreateTargetMesh(FBXUVBodyGeometry target)
        {
            var vertices = new Vector3[target.Uvs.Length];
            for (var i = 0; i < vertices.Length; i++) vertices[i] = new Vector3(target.Uvs[i].x, target.Uvs[i].y, 0f);
            var mesh = new Mesh
            {
                name = "FBXUV Body Target Mask",
                hideFlags = HideFlags.HideAndDontSave,
                indexFormat = vertices.Length > ushort.MaxValue ? IndexFormat.UInt32 : IndexFormat.UInt16,
            };
            try
            {
                mesh.SetVertices(vertices);
                mesh.SetIndices(target.Triangles, MeshTopology.Triangles, 0, true);
                mesh.UploadMeshData(true);
                return mesh;
            }
            catch
            {
                UnityEngine.Object.DestroyImmediate(mesh);
                throw;
            }
        }

        public void Dispose()
        {
            SourceMask?.Dispose();
            SourceMask = null;
            if (Map != null) UnityEngine.Object.DestroyImmediate(Map);
            if (Islands != null) UnityEngine.Object.DestroyImmediate(Islands);
            if (Quad != null) UnityEngine.Object.DestroyImmediate(Quad);
            if (TargetMesh != null) UnityEngine.Object.DestroyImmediate(TargetMesh);
            Map = null;
            Islands = null;
            Quad = null;
            TargetMesh = null;
        }
    }
}
