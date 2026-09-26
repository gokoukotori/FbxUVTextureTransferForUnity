using System.Collections.Generic;

namespace GokouKotori.FBXUVTextureTransfer
{
    // Managed, nonserialized state owned by one binding. Keep only the two most
    // recently used sizes/orientations (Inspector thumbnail and avatar preview).
    internal sealed class FBXUVRegionComputationCache
    {
        private FBXUVTransferRegion source, target;
        private Entry recent, previous;
        internal List<FBXUVTriangle> SourceTriangles { get; private set; }
        internal List<FBXUVTriangle> TargetTriangles { get; private set; }

        internal void Prepare(FBXUVTransferRegion currentSource, FBXUVTransferRegion currentTarget)
        {
            if (SameRegion(source, currentSource) && SameRegion(target, currentTarget)) return;
            source = Snapshot(currentSource);
            target = Snapshot(currentTarget);
            SourceTriangles = FBXUVDeformationUtility.GetEffectiveTriangles(source);
            TargetTriangles = FBXUVDeformationUtility.GetEffectiveTriangles(target);
            recent = previous = null;
        }

        internal Dictionary<FBXUVDeformationUtility.VertexKey, FBXUVDeformedVertex> GetVertexMap(
            int width, int height, FBXUVTransferOrientation orientation)
        {
            if (recent != null && recent.Matches(width, height, orientation)) return recent.Map;
            if (previous != null && previous.Matches(width, height, orientation))
            {
                var swap = recent;
                recent = previous;
                previous = swap;
                return recent.Map;
            }
            var map = FBXUVDeformationUtility.CreateKeyedVertexMap(source, target, width, height, orientation);
            previous = recent;
            recent = new Entry(width, height, orientation, map);
            return map;
        }

        private static bool SameRegion(FBXUVTransferRegion snapshot, FBXUVTransferRegion current)
        {
            return snapshot != null && snapshot.bounds.Equals(current.bounds)
                && SameTriangles(snapshot.triangles, current.triangles);
        }

        private static bool SameTriangles(List<FBXUVTriangle> saved, List<FBXUVTriangle> current)
        {
            if (saved == null || current == null) return saved == current;
            if (saved.Count != current.Count) return false;
            for (var i = 0; i < saved.Count; i++)
            {
                var a = saved[i]; var b = current[i];
                if (a == null || b == null) { if (a != b) return false; }
                else if (a.index != b.index || !a.a.Equals(b.a) || !a.b.Equals(b.b) || !a.c.Equals(b.c)) return false;
            }
            return true;
        }

        private static FBXUVTransferRegion Snapshot(FBXUVTransferRegion region)
        {
            var triangles = FBXUVMakeupComputationCache.CopyTriangles(region.triangles);
            return new FBXUVTransferRegion
            {
                bounds = region.bounds,
                triangles = triangles == null ? null : new List<FBXUVTriangle>(triangles)
            };
        }

        private sealed class Entry
        {
            private readonly int width, height;
            private readonly FBXUVTransferOrientation orientation;
            internal readonly Dictionary<FBXUVDeformationUtility.VertexKey, FBXUVDeformedVertex> Map;

            internal Entry(int width, int height, FBXUVTransferOrientation orientation,
                Dictionary<FBXUVDeformationUtility.VertexKey, FBXUVDeformedVertex> map)
            {
                this.width = width;
                this.height = height;
                this.orientation = orientation;
                Map = map;
            }

            internal bool Matches(int candidateWidth, int candidateHeight, FBXUVTransferOrientation candidateOrientation)
                => width == candidateWidth && height == candidateHeight && orientation == candidateOrientation;
        }
    }
}
