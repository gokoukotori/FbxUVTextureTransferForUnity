using UnityEngine;

namespace GokouKotori.FBXUVTextureTransfer
{
    // Numeric helpers shared by the transfer types. Helpers whose precision or tolerances must match a shader stay with
    // their caller (for example FBXUVEyeFrame.Cross, which multiplies in float).
    internal static class FBXUVMath
    {
        internal static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        internal static bool Finite(Vector2 value) => Finite(value.x) && Finite(value.y);

        internal static bool Finite(Vector3 value) => Finite(value.x) && Finite(value.y) && Finite(value.z);

        // 2D cross product a.x * b.y - a.y * b.x, with both products formed in double.
        internal static double CrossDouble(Vector2 a, Vector2 b) => (double)a.x * b.y - (double)a.y * b.x;

        // Cell of a [0, 1] coordinate in a lookup grid with gridSize cells per side. Outside values clamp to the edge.
        internal static int UnitGridCell(float value, int gridSize) =>
            Mathf.Clamp(Mathf.FloorToInt(value * gridSize), 0, gridSize - 1);
    }
}
