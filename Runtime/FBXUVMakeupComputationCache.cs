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
                || !SameTriangles(sourceTriangles, layer.sourceRegion?.triangles)
                || !SameTriangles(targetTriangles, layer.targetRegion?.triangles))
            {
                mapping = null;
                mappingFailure = null;
                smoothing = layer.warpSmoothing;
                points = layer.landmarks?.ConvertAll(p => p == null ? null : new FBXUVMakeupLandmark
                    { name = p.name, sourceUv = p.sourceUv, targetUv = p.targetUv }).ToArray();
                sourceTriangles = CopyTriangles(layer.sourceRegion?.triangles);
                targetTriangles = CopyTriangles(layer.targetRegion?.triangles);
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

        internal static FBXUVTriangle[] CopyTriangles(List<FBXUVTriangle> triangles) =>
            triangles?.ConvertAll(t => t == null ? null : new FBXUVTriangle(t.index, t.a, t.b, t.c)).ToArray();

        internal static bool SameTriangles(FBXUVTriangle[] saved, List<FBXUVTriangle> current)
        {
            if (saved == null || current == null) return saved == null && current == null;
            if (saved.Length != current.Count) return false;
            for (var i = 0; i < saved.Length; i++)
            {
                var a = saved[i]; var b = current[i];
                if (a == null || b == null) { if (a != b) return false; }
                else if (a.index != b.index || !a.a.Equals(b.a) || !a.b.Equals(b.b) || !a.c.Equals(b.c)) return false;
            }
            return true;
        }
    }
}
