using System;
using System.Collections.Generic;
using UnityEngine;

namespace GokouKotori.FBXUVTextureTransfer
{
    /// <summary>The basic target-to-source mapping, shared by rendering and inspection.</summary>
    public sealed class FBXUVMakeupMapping
    {
        public FBXUVMakeupWarp Warp { get; private set; }
        public FBXUVMakeupExactMap Exact { get; private set; }
        public FBXUVMakeupExactMap Mouth { get; private set; }
        public string BoundaryReason { get; private set; }
        public string Mode => Exact == null ? "TPS" : Mouth == null ? "Exact" : "Exact+Mouth";

        public static FBXUVMakeupMapping Create(FBXUVMakeupTransferLayer layer)
        {
            if (layer == null) throw new ArgumentNullException(nameof(layer));
            if (!layer.TryCreateWarp(out var warp, out var reason)) throw new ArgumentException(reason);
            var result = new FBXUVMakeupMapping { Warp = warp };
            // Regularization changes the fitted positions, never the mapping algorithm.
            // Keep the serialized controls intact, including at zero regularization.
            var fitted = new List<FBXUVMakeupLandmark>(layer.landmarks.Count);
            for (var i = 0; i < layer.landmarks.Count; i++)
            {
                var point = layer.landmarks[i];
                // Image-corner anchors define the domain and must not drift outside
                // the texture while interior feature positions are regularized.
                var isCorner = (point.targetUv.x == 0f || point.targetUv.x == 1f)
                    && (point.targetUv.y == 0f || point.targetUv.y == 1f);
                fitted.Add(new FBXUVMakeupLandmark {
                    name = point.name, targetUv = point.targetUv,
                    sourceUv = isCorner ? point.sourceUv : warp.FittedControl(i, point.sourceUv)
                });
            }
            result.Exact = FBXUVMakeupExactMap.Create(fitted, layer.targetRegion?.triangles);
            FBXUVMakeupBoundaryMap.TryCreate(layer, result.Exact, out var mouth, out reason);
            result.Mouth = mouth;
            result.BoundaryReason = reason;
            return result;
        }

        public Vector2 Evaluate(Vector2 target)
        {
            if (Mouth != null && Mouth.TryEvaluate(target, out var local)) return local;
            if (Exact == null) return Warp.Evaluate(target);
            if (Exact.TryEvaluate(target, out var source)) return source;
            throw new ArgumentException("基本写像の範囲外です。");
        }
    }
}
