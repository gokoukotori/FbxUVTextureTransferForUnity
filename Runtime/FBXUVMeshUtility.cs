using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Unity.Profiling;
using UnityEngine;

[assembly: InternalsVisibleTo("GokouKotori.FBXUVTextureTransfer.Editor")]
[assembly: InternalsVisibleTo("GokouKotori.FBXUVTextureTransfer.Tests.Editor")]

namespace GokouKotori.FBXUVTextureTransfer
{
    public static class FBXUVMeshUtility
    {
        public const int MinimumUvChannel = 0;
        public const int MaximumUvChannel = 7;

        public static List<FBXUVTriangle> ExtractTriangles(Mesh mesh, int subMeshIndex, int uvChannel)
        {
            return new FBXUVMeshAnalysisCache().Get(mesh, subMeshIndex, uvChannel).GetTriangles();
        }

        public static string ComputeMeshContentHash(Mesh mesh, int subMeshIndex, int uvChannel)
        {
            return new FBXUVMeshAnalysisCache().Get(mesh, subMeshIndex, uvChannel).GetContentHash();
        }

        public static bool IsMeshHashCurrent(FBXUVTransferRegion region)
        {
            return IsMeshHashCurrent(region, new FBXUVMeshAnalysisCache());
        }

        internal static bool IsMeshHashCurrent(FBXUVTransferRegion region, FBXUVMeshAnalysisCache analysisCache)
        {
            if (region == null || region.mesh == null || string.IsNullOrEmpty(region.meshHash)) return false;
            try
            {
                return string.Equals(
                    region.meshHash,
                    analysisCache.Get(region.mesh, region.subMeshIndex, region.uvChannel).GetContentHash(),
                    StringComparison.Ordinal);
            }
            catch (Exception)
            {
                return false;
            }
        }

        internal static void ValidateArguments(Mesh mesh, int subMeshIndex, int uvChannel)
        {
            if (mesh == null) throw new ArgumentNullException(nameof(mesh));
            if (subMeshIndex < 0 || subMeshIndex >= mesh.subMeshCount) throw new ArgumentOutOfRangeException(nameof(subMeshIndex));
            if (uvChannel < MinimumUvChannel || uvChannel > MaximumUvChannel) throw new ArgumentOutOfRangeException(nameof(uvChannel));
        }
    }

    internal sealed class FBXUVMeshAnalysisCache
    {
        private static readonly ProfilerMarker AnalysisMarker =
            new ProfilerMarker("FBXUVTextureTransfer.MeshAnalysis.Snapshot");

        private readonly Dictionary<MeshAnalysisKey, FBXUVMeshAnalysis> entries =
            new Dictionary<MeshAnalysisKey, FBXUVMeshAnalysis>();

        internal int AnalysisCount { get; private set; }

        internal FBXUVMeshAnalysis Get(Mesh mesh, int subMeshIndex, int uvChannel)
        {
            var key = new MeshAnalysisKey(mesh, subMeshIndex, uvChannel);
            if (entries.TryGetValue(key, out var analysis)) return analysis;

            using (AnalysisMarker.Auto())
            {
                analysis = new FBXUVMeshAnalysis(mesh, subMeshIndex, uvChannel);
            }
            entries.Add(key, analysis);
            AnalysisCount++;
            return analysis;
        }

        private readonly struct MeshAnalysisKey : IEquatable<MeshAnalysisKey>
        {
            private readonly Mesh mesh;
            private readonly int subMeshIndex;
            private readonly int uvChannel;

            internal MeshAnalysisKey(Mesh mesh, int subMeshIndex, int uvChannel)
            {
                this.mesh = mesh;
                this.subMeshIndex = subMeshIndex;
                this.uvChannel = uvChannel;
            }

            public bool Equals(MeshAnalysisKey other)
            {
                return ReferenceEquals(mesh, other.mesh)
                    && subMeshIndex == other.subMeshIndex
                    && uvChannel == other.uvChannel;
            }

            public override bool Equals(object obj)
            {
                return obj is MeshAnalysisKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    var meshHash = ReferenceEquals(mesh, null) ? 0 : RuntimeHelpers.GetHashCode(mesh);
                    return ((meshHash * 397) ^ subMeshIndex) * 397 ^ uvChannel;
                }
            }
        }
    }

    internal sealed class FBXUVMeshAnalysis
    {
        private static readonly ProfilerMarker TriangleMarker =
            new ProfilerMarker("FBXUVTextureTransfer.MeshAnalysis.Triangles");
        private static readonly ProfilerMarker HashMarker =
            new ProfilerMarker("FBXUVTextureTransfer.MeshAnalysis.Hash");

        private readonly int subMeshIndex;
        private readonly int uvChannel;
        private List<Vector2> uvs;
        private int[] indices;
        private Exception failure;
        private Exception triangleFailure;
        private Exception hashFailure;
        private List<FBXUVTriangle> triangles;
        private string contentHash;
        private int vertexCount;

        internal FBXUVMeshAnalysis(Mesh mesh, int subMeshIndex, int uvChannel)
        {
            this.subMeshIndex = subMeshIndex;
            this.uvChannel = uvChannel;

            try
            {
                FBXUVMeshUtility.ValidateArguments(mesh, subMeshIndex, uvChannel);
                if (mesh.GetTopology(subMeshIndex) != MeshTopology.Triangles)
                {
                    throw new ArgumentException(
                        "指定されたsubmeshのtopologyはTrianglesではありません。",
                        nameof(subMeshIndex));
                }

                vertexCount = mesh.vertexCount;
                uvs = new List<Vector2>(vertexCount);
                mesh.GetUVs(uvChannel, uvs);
                if (uvs.Count != vertexCount)
                {
                    throw new ArgumentException(
                        "指定されたUV channelには全頂点分のUVがありません。",
                        nameof(uvChannel));
                }

                indices = mesh.GetIndices(subMeshIndex, true);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        }

        internal List<FBXUVTriangle> GetTriangles()
        {
            ThrowIfFailed();
            if (triangleFailure != null) throw triangleFailure;
            if (triangles != null) return triangles;

            using (TriangleMarker.Auto())
            {
                try
                {
                    var extractedTriangles = new List<FBXUVTriangle>(indices.Length / 3);
                    for (var offset = 0; offset + 2 < indices.Length; offset += 3)
                    {
                        extractedTriangles.Add(new FBXUVTriangle(
                            offset / 3,
                            uvs[indices[offset]],
                            uvs[indices[offset + 1]],
                            uvs[indices[offset + 2]]));
                    }
                    triangles = extractedTriangles;
                }
                catch (Exception exception)
                {
                    triangleFailure = exception;
                    throw;
                }
            }
            return triangles;
        }

        internal string GetContentHash()
        {
            ThrowIfFailed();
            if (hashFailure != null) throw hashFailure;
            if (contentHash != null) return contentHash;

            using (HashMarker.Auto())
            {
                try
                {
                    using (var stream = new MemoryStream())
                    using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
                    {
                        writer.Write(1); // hash format version
                        writer.Write(subMeshIndex);
                        writer.Write(uvChannel);
                        writer.Write(vertexCount);
                        writer.Write(uvs.Count);
                        for (var index = 0; index < uvs.Count; index++)
                        {
                            writer.Write(uvs[index].x);
                            writer.Write(uvs[index].y);
                        }
                        writer.Write(indices.Length);
                        for (var index = 0; index < indices.Length; index++)
                        {
                            writer.Write(indices[index]);
                        }

                        writer.Flush();
                        stream.Position = 0;
                        using (var sha256 = SHA256.Create())
                        {
                            var hash = sha256.ComputeHash(stream);
                            var builder = new StringBuilder(hash.Length * 2);
                            for (var index = 0; index < hash.Length; index++)
                            {
                                builder.Append(hash[index].ToString("x2"));
                            }
                            contentHash = builder.ToString();
                        }
                    }
                }
                catch (Exception exception)
                {
                    hashFailure = exception;
                    throw;
                }
            }
            return contentHash;
        }

        private void ThrowIfFailed()
        {
            if (failure != null) throw failure;
        }
    }
}
