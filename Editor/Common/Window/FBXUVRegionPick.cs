using System;
using UnityEngine;

namespace GokouKotori.FBXUVTextureTransfer.Editor
{
    internal readonly struct FBXUVRegionPick
    {
        internal readonly Mesh Mesh;
        internal readonly int SubMeshIndex;
        internal readonly int UvChannel;
        internal readonly string RendererPath;
        internal readonly FBXUVIsland Island;

        internal FBXUVRegionPick(Mesh mesh, int subMeshIndex, int uvChannel, string rendererPath, FBXUVIsland island)
        {
            Mesh = mesh;
            SubMeshIndex = subMeshIndex;
            UvChannel = uvChannel;
            RendererPath = rendererPath;
            Island = island;
        }
    }

    internal readonly struct FBXUVSavedRegion : IEquatable<FBXUVSavedRegion>
    {
        internal static readonly FBXUVSavedRegion Empty = new FBXUVSavedRegion(null, 0, 0, -1, string.Empty, null);

        internal readonly Mesh Mesh;
        internal readonly int SubMeshIndex;
        internal readonly int UvChannel;
        internal readonly int IslandId;
        internal readonly string MeshHash;
        // null = no renderer information (by-mesh windows); "" = the root renderer (Eye).
        internal readonly string RendererPath;

        private FBXUVSavedRegion(Mesh mesh, int subMeshIndex, int uvChannel, int islandId, string meshHash, string rendererPath)
        {
            Mesh = mesh;
            SubMeshIndex = subMeshIndex;
            UvChannel = uvChannel;
            IslandId = islandId;
            MeshHash = meshHash ?? string.Empty;
            RendererPath = rendererPath;
        }

        internal bool HasMesh => Mesh != null;

        internal static FBXUVSavedRegion From(FBXUVTransferRegion region, string rendererPath = null)
        {
            if (region == null)
            {
                return Empty;
            }

            return new FBXUVSavedRegion(
                region.Mesh,
                region.SubMeshIndex,
                region.UvChannel,
                region.IslandId,
                region.MeshHash,
                rendererPath);
        }

        public bool Equals(FBXUVSavedRegion other)
        {
            return ReferenceEquals(Mesh, other.Mesh)
                && SubMeshIndex == other.SubMeshIndex
                && UvChannel == other.UvChannel
                && IslandId == other.IslandId
                && string.Equals(MeshHash, other.MeshHash, StringComparison.Ordinal)
                && string.Equals(RendererPath, other.RendererPath, StringComparison.Ordinal);
        }

        public override bool Equals(object obj)
        {
            return obj is FBXUVSavedRegion other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = Mesh != null ? Mesh.GetHashCode() : 0;
                hash = hash * 31 + SubMeshIndex;
                hash = hash * 31 + UvChannel;
                hash = hash * 31 + IslandId;
                hash = hash * 31 + StringComparer.Ordinal.GetHashCode(MeshHash);
                hash = hash * 31 + (RendererPath != null ? StringComparer.Ordinal.GetHashCode(RendererPath) : 0);
                return hash;
            }
        }
    }
}
