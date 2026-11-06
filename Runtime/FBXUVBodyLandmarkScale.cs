using System;
using UnityEngine;

namespace GokouKotori.FBXUVTextureTransfer
{
    // Scales the along-surface component about a landmark centre: exactly by the scale inside the core radius,
    // fading with a smoothstep to nothing at SupportFactor times the radius. Points beyond that are copied as is.
    // The scale is exact (one-to-one) only for [MinScale, MaxScale] with a unit (or normalizable) normal; Apply does not clamp, callers check k.
    internal static class FBXUVBodyLandmarkScale
    {
        internal const float MinScale = 0.7f;
        internal const float MaxScale = 1.4f;
        internal const float SupportFactor = 3f;
        internal const float NegligibleScale = 0.001f;

        internal static Vector3[] Apply(Vector3[] positions, Vector3 centre, Vector3 normal, float coreRadius, float scale)
        {
            if (positions == null) throw new ArgumentNullException(nameof(positions));
            var result = new Vector3[positions.Length];
            var n = normal.normalized;
            var support = SupportFactor * coreRadius;
            for (var i = 0; i < positions.Length; i++)
            {
                var x = positions[i];
                var v = x - centre;
                var r = v.magnitude;
                if (!(r < support))
                {
                    result[i] = x;
                    continue;
                }
                var tangent = v - Vector3.Dot(v, n) * n;
                float psi;
                if (r <= coreRadius) psi = 1f;
                else
                {
                    var s = (r - coreRadius) / (2f * coreRadius);
                    psi = 1f - (3f * s * s - 2f * s * s * s);
                }
                result[i] = x + (scale - 1f) * psi * tangent;
            }
            return result;
        }
    }
}
