using System.Collections.Generic;

namespace GokouKotori.FBXUVTextureTransfer.Editor
{
    internal static class FBXUVRegionFactory
    {
        internal static FBXUVTransferRegion Create(in FBXUVRegionPick pick, FBXUVMeshAnalysisCache cache)
        {
            var island = pick.Island;
            return new FBXUVTransferRegion
            {
                Mesh = pick.Mesh,
                SubMeshIndex = pick.SubMeshIndex,
                UvChannel = pick.UvChannel,
                IslandId = island.Id,
                Triangles = new List<FBXUVTriangle>(island.Triangles),
                Bounds = island.Bounds,
                MeshHash = cache.Get(pick.Mesh, pick.SubMeshIndex, pick.UvChannel).GetContentHash()
            };
        }
    }
}
