using System;

namespace GokouKotori.FBXUVTextureTransfer
{
    // The numerics of FBXUVBodyPinWarp: the Wendland C2 kernel and the small dense weight system.
    internal static class FBXUVBodyPinSolver
    {
        // max |φ'(t)| for φ(t) = (1 − t)^4 (4t + 1): φ'(t) = −20 t (1 − t)^3, largest at t = 1/4.
        internal const double KernelSlope = 135.0 / 64.0;
        private const double SingularPivot = 1e-12;

        // Wendland C2: (1 − t)^4 (4t + 1) for t < 1, zero beyond.
        internal static double Kernel(double t)
        {
            if (t >= 1.0) return 0.0;
            var a = 1.0 - t;
            return a * a * a * a * (4.0 * t + 1.0);
        }

        // Gaussian elimination with partial pivoting on [A | B] (n × n, then three columns); B becomes the solution.
        // False when a pivot is (near) zero or not a number.
        internal static bool Solve(double[,] system, int n)
        {
            var columns = n + 3;
            for (var col = 0; col < n; col++)
            {
                var pivot = col;
                for (var row = col + 1; row < n; row++)
                {
                    if (Math.Abs(system[row, col]) > Math.Abs(system[pivot, col])) pivot = row;
                }
                if (!(Math.Abs(system[pivot, col]) > SingularPivot)) return false;
                if (pivot != col)
                {
                    for (var j = col; j < columns; j++)
                    {
                        var swap = system[col, j];
                        system[col, j] = system[pivot, j];
                        system[pivot, j] = swap;
                    }
                }
                for (var row = col + 1; row < n; row++)
                {
                    var factor = system[row, col] / system[col, col];
                    if (factor == 0.0) continue;
                    for (var j = col; j < columns; j++) system[row, j] -= factor * system[col, j];
                }
            }
            for (var col = n - 1; col >= 0; col--)
            for (var j = n; j < columns; j++)
            {
                var value = system[col, j];
                for (var k = col + 1; k < n; k++) value -= system[col, k] * system[k, j];
                system[col, j] = value / system[col, col];
            }
            return true;
        }
    }
}
