using System;
using System.Collections.Generic;

namespace GokouKotori.FBXUVTextureTransfer
{
    // One entry per layer, owned by the layer and never serialized. Compare values,
    // not object identity: editor Undo and scripts can mutate lists in place.
    internal sealed class FBXUVMakeupComputationCache
    {
        private FBXUVMakeupLandmark[] points;
        private FBXUVTriangle[] sourceTriangles, targetTriangles;
        private float smoothing;
        private FBXUVMakeupMapping mapping;
        private Exception mappingFailure;

        internal FBXUVMakeupMapping GetMapping(FBXUVMakeupTransferLayer layer)
        {
            if (points == null || !smoothing.Equals(layer.warpSmoothing)
                || !SamePoints(layer.landmarks)
                || !FBXUVTriangleSnapshot.Same(sourceTriangles, layer.sourceRegion?.triangles)
                || !FBXUVTriangleSnapshot.Same(targetTriangles, layer.targetRegion?.triangles))
            {
                mapping = null;
                mappingFailure = null;
                smoothing = layer.warpSmoothing;
                points = layer.landmarks?.ConvertAll(p => p == null ? null : new FBXUVMakeupLandmark
                    { name = p.name, sourceUv = p.sourceUv, targetUv = p.targetUv }).ToArray();
                sourceTriangles = FBXUVTriangleSnapshot.Copy(layer.sourceRegion?.triangles);
                targetTriangles = FBXUVTriangleSnapshot.Copy(layer.targetRegion?.triangles);
                try { mapping = FBXUVMakeupMapping.Create(layer); }
                catch (ArgumentException exception) { mappingFailure = exception; }
                catch { points = null; throw; }
            }
            if (mappingFailure != null) throw new ArgumentException(mappingFailure.Message, mappingFailure);
            return mapping;
        }

        private bool SamePoints(List<FBXUVMakeupLandmark> current)
        {
            if (current == null || points.Length != current.Count) return false;
            for (var i = 0; i < points.Length; i++)
            {
                var a = points[i]; var b = current[i];
                if (a == null || b == null) { if (a != b) return false; }
                else if (a.name != b.name || !a.sourceUv.Equals(b.sourceUv) || !a.targetUv.Equals(b.targetUv)) return false;
            }
            return true;
        }
    }
}
