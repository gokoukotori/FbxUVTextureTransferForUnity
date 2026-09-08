using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
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
}
