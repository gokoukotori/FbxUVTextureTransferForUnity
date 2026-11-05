using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace GokouKotori.FBXUVTextureTransfer
{
    [Serializable]
    public sealed class FBXUVBodyPin
    {
        // Target UVs. The 3D quantities are derived from the mesh on every evaluation and never saved.
        public Vector2 origin;
        public Vector2 position;
        // Influence radius in meters, independent of the avatar's height.
        public float radius = .05f;
        public bool fixedPoint;
    }

    [Serializable]
    public sealed class FBXUVBodyManualSettings
    {
        public bool enabled;
        public List<FBXUVBodyPin> pins = new List<FBXUVBodyPin>();

        internal FBXUVBodyManualSettings Copy() => new FBXUVBodyManualSettings
        {
            enabled = enabled,
            pins = pins?.Select(p => p == null ? null : new FBXUVBodyPin
                { origin = p.origin, position = p.position, radius = p.radius, fixedPoint = p.fixedPoint }).ToList()
                ?? new List<FBXUVBodyPin>()
        };
    }
}
