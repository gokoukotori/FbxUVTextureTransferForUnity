using System;
using System.Collections.Generic;
using UnityEngine;

namespace GokouKotori.FBXUVTextureTransfer
{
    // 乳首のシェイプキーの範囲を単位円盤に並べる（2026-11-06 範囲どうしの設計書 §3）。
    // 頂点は位置で束ね（FBXUVBodyWeld.Groups）、束ごとに円盤上の座標を持つ。中心の束は (0,0)、縁の束は単位円の上。
    internal sealed class FBXUVBodyRegionDisc
    {
        internal const string HoleReason = "穴があります";
        internal const string CentreOutsideReason = "中心が範囲の外です";
        internal const string FoldReason = "縁が折り返しています";
        internal const string NotConvergedReason = "計算が収束しません";
        internal const string FlippedReason = "三角形が裏返ります";

        // |Σ Δθ| と 2π の違いの上限（rad）。
        private const double TurnTolerance = 1e-3;
        private const double Omega = 1.8;
        private const double FallbackOmega = 1.0;
        // 変化の最大値が、これだけ続けて増えたら発散とみなす。
        private const int GrowthLimit = 50;
        // 1 回のくり返しでの座標の変化（円盤の単位）がこれ未満になったら止める。
        private const double ChangeTolerance = 1e-7;
        private const int MaximumSweeps = 50000;
        private const double LocateTolerance = -1e-6;

        // 範囲の三角形（geometry.Triangles の 3 つおきの位置 ÷ 3）。
        internal readonly int[] Triangles;
        // 頂点 → 束（geometry 全体の FBXUVBodyWeld.Groups）。
        internal readonly int[] WeldOf;
        // 範囲の束 → 円盤上の座標（double で解き、float で持つ）。
        internal readonly IReadOnlyDictionary<int, Vector2> Coordinates;
        internal readonly int CentreGroup;
        // 縁の束。θ が増える向きの順。
        internal readonly int[] Rim;
        // SOR のくり返し回数（止まった回を含む）。
        internal readonly int Sweeps;
        // 収まった ω（1.8、収まらなければ 1.0）。
        internal readonly double UsedOmega;

        // 範囲の三角形 k の角の円盤上の座標（corners[k * 3 + i] は geometry.Triangles[Triangles[k] * 3 + i] の頂点）。
        private readonly Vector2[] corners;
        private readonly object gridLock = new object();
        private volatile LocateGrid grid;

        private FBXUVBodyRegionDisc(int[] triangles, int[] weldOf, Dictionary<int, Vector2> coordinates, int centreGroup, int[] rim,
            int sweeps, double omega, Vector2[] corners)
        {
            Triangles = triangles;
            WeldOf = weldOf;
            Coordinates = coordinates;
            CentreGroup = centreGroup;
            Rim = rim;
            Sweeps = sweeps;
            UsedOmega = omega;
            this.corners = corners;
        }

        internal Vector2 CoordinateOf(int vertex) => Coordinates[WeldOf[vertex]];

        // 円盤上で point を含む範囲の三角形を探す（重心座標がすべて −1e-6 以上）。探し直しはしない。
        // triangle は geometry の三角形の番号（Triangles の値）、barycentric は geometry.Triangles[triangle * 3 + 0..2] の頂点の重み。
        internal bool TryLocate(Vector2 point, out int triangle, out Vector3 barycentric)
        {
            triangle = -1;
            barycentric = Vector3.zero;
            if (!(point.x >= -1f && point.x <= 1f && point.y >= -1f && point.y <= 1f)) return false;

            var lookup = Grid();
            var range = lookup.Ranges[Cell(point.y, lookup.Size) * lookup.Size + Cell(point.x, lookup.Size)];
            for (var n = range.x; n < range.x + range.y; n++)
            {
                var k = lookup.Indices[n];
                if (!Barycentric(point, corners[k * 3], corners[k * 3 + 1], corners[k * 3 + 2], out var wa, out var wb, out var wc)) continue;
                if (wa < LocateTolerance || wb < LocateTolerance || wc < LocateTolerance) continue;
                triangle = Triangles[k];
                barycentric = new Vector3((float)wa, (float)wb, (float)wc);
                return true;
            }

            return false;
        }

        // 円盤にできる条件（§3.2）を順に確かめ、縁を角度で円周に置き（§3.3）、中を平均値座標で解く（§3.4）。
        // centreVertex は呼び出し側が決めた中心の頂点（辺の集まる点の束が範囲にあればその頂点、無ければ region.Vertex）。
        internal static bool TryBuild(FBXUVBodyGeometry geometry, FBXUVBodyNippleRegion region, int centreVertex,
            out FBXUVBodyRegionDisc disc, out string reason)
        {
            if (geometry == null) throw new ArgumentNullException(nameof(geometry));
            if (region == null) throw new ArgumentNullException(nameof(region));
            disc = null;
            reason = null;
            var positions = geometry.Positions;
            var triangles = geometry.Triangles;
            var regionTriangles = (int[])region.Triangles.Clone();
            var weldOf = FBXUVBodyWeld.Groups(geometry, out _);

            // 1. 縁（1つの範囲の三角形にしか使われない辺）が1本の閉じた輪。
            var groups = new HashSet<int>();
            var edgeUse = new Dictionary<long, int>();
            foreach (var t in regionTriangles)
            {
                for (var i = 0; i < 3; i++)
                {
                    int a = weldOf[triangles[t * 3 + i]], b = weldOf[triangles[t * 3 + (i + 1) % 3]];
                    groups.Add(a);
                    if (a == b) continue;
                    var key = EdgeKey(a, b);
                    edgeUse[key] = edgeUse.TryGetValue(key, out var used) ? used + 1 : 1;
                }
            }

            var rimNeighbours = new Dictionary<int, List<int>>();
            foreach (var pair in edgeUse)
            {
                if (pair.Value >= 3)
                {
                    reason = HoleReason;
                    return false;
                }

                if (pair.Value != 1) continue;
                int a = (int)(pair.Key >> 32), b = (int)(pair.Key & 0xFFFFFFFFL);
                AddNeighbour(rimNeighbours, a, b);
                AddNeighbour(rimNeighbours, b, a);
            }

            var start = int.MaxValue;
            foreach (var pair in rimNeighbours)
            {
                if (pair.Value.Count != 2)
                {
                    reason = HoleReason;
                    return false;
                }

                start = Math.Min(start, pair.Key);
            }

            var loop = new List<int>();
            if (rimNeighbours.Count > 0)
            {
                var previous = start;
                var current = rimNeighbours[start][0];
                loop.Add(start);
                while (current != start && loop.Count <= rimNeighbours.Count)
                {
                    loop.Add(current);
                    var next = rimNeighbours[current][0] == previous ? rimNeighbours[current][1] : rimNeighbours[current][0];
                    previous = current;
                    current = next;
                }
            }

            if (loop.Count < 3 || loop.Count != rimNeighbours.Count)
            {
                reason = HoleReason;
                return false;
            }

            // 2. 中心の束が範囲の頂点で、縁の上に無い。
            var centreGroup = centreVertex >= 0 && centreVertex < weldOf.Length ? weldOf[centreVertex] : -1;
            if (!groups.Contains(centreGroup) || rimNeighbours.ContainsKey(centreGroup))
            {
                reason = CentreOutsideReason;
                return false;
            }

            // 3. 縁の角度が一方向にだけ進み、ちょうど1周する。
            var groupPositions = GroupPositions(regionTriangles, triangles, positions, weldOf);
            CentreFrame(geometry, weldOf, centreGroup, out var centre, out var normal);
            var up = Subtract(new[] { 0.0, 1.0, 0.0 }, Scale(normal, Dot(new[] { 0.0, 1.0, 0.0 }, normal)));
            var upLength = Math.Sqrt(Dot(up, up));
            // 法線が無い・真上を向くときは角度が決まらないので、折り返しと同じく円盤にしない。
            if (!(upLength > 0.0) || !(Dot(normal, normal) > 0.0))
            {
                reason = FoldReason;
                return false;
            }

            up = Scale(up, 1.0 / upLength);
            var side = Cross(normal, up);
            var angles = new double[loop.Count];
            for (var k = 0; k < loop.Count; k++)
            {
                var d = Subtract(groupPositions[loop[k]], centre);
                angles[k] = Math.Atan2(Dot(d, side), Dot(d, up));
            }

            var sign = 0;
            var turn = 0.0;
            for (var k = 0; k < loop.Count; k++)
            {
                var delta = angles[(k + 1) % loop.Count] - angles[k];
                while (delta > Math.PI) delta -= 2.0 * Math.PI;
                while (delta <= -Math.PI) delta += 2.0 * Math.PI;
                var step = delta > 0.0 ? 1 : delta < 0.0 ? -1 : 0;
                if (step == 0 || (sign != 0 && step != sign))
                {
                    reason = FoldReason;
                    return false;
                }

                sign = step;
                turn += delta;
            }

            if (!(Math.Abs(Math.Abs(turn) - 2.0 * Math.PI) < TurnTolerance))
            {
                reason = FoldReason;
                return false;
            }

            var rim = loop.ToArray();
            if (sign < 0)
            {
                Array.Reverse(rim);
                Array.Reverse(angles);
            }

            // 中の置き方：中心は (0,0)、縁は (cos θ, sin θ) に固定し、ほかの束を平均値座標の重み付き平均として SOR で解く。
            var slotOf = new Dictionary<int, int>();
            var order = new List<int>(groups);
            order.Sort();
            foreach (var g in order) slotOf[g] = slotOf.Count;
            var xs = new double[order.Count];
            var ys = new double[order.Count];
            var fixedSlot = new bool[order.Count];
            fixedSlot[slotOf[centreGroup]] = true;
            for (var k = 0; k < rim.Length; k++)
            {
                var slot = slotOf[rim[k]];
                fixedSlot[slot] = true;
                xs[slot] = Math.Cos(angles[k]);
                ys[slot] = Math.Sin(angles[k]);
            }

            var rows = new Dictionary<int, double>[order.Count];
            foreach (var t in regionTriangles)
            {
                for (var i = 0; i < 3; i++)
                {
                    var self = slotOf[weldOf[triangles[t * 3 + i]]];
                    if (fixedSlot[self]) continue;
                    var j = slotOf[weldOf[triangles[t * 3 + (i + 1) % 3]]];
                    var k = slotOf[weldOf[triangles[t * 3 + (i + 2) % 3]]];
                    var origin = groupPositions[order[self]];
                    var toJ = Subtract(groupPositions[order[j]], origin);
                    var toK = Subtract(groupPositions[order[k]], origin);
                    double lengthJ = Math.Sqrt(Dot(toJ, toJ)), lengthK = Math.Sqrt(Dot(toK, toK));
                    var cross = Cross(toJ, toK);
                    // tan(α/2) = |a × b| / (|a||b| + a·b)。
                    var halfTangent = Math.Sqrt(Dot(cross, cross)) / (lengthJ * lengthK + Dot(toJ, toK));
                    var row = rows[self] ?? (rows[self] = new Dictionary<int, double>());
                    row[j] = (row.TryGetValue(j, out var wj) ? wj : 0.0) + halfTangent / lengthJ;
                    row[k] = (row.TryGetValue(k, out var wk) ? wk : 0.0) + halfTangent / lengthK;
                }
            }

            var unknowns = new List<int>();
            var neighbourSlots = new int[order.Count][];
            var neighbourWeights = new double[order.Count][];
            for (var slot = 0; slot < order.Count; slot++)
            {
                if (fixedSlot[slot]) continue;
                unknowns.Add(slot);
                var row = rows[slot];
                var sum = 0.0;
                foreach (var w in row.Values) sum += w;
                neighbourSlots[slot] = new int[row.Count];
                neighbourWeights[slot] = new double[row.Count];
                var n = 0;
                foreach (var pair in row)
                {
                    neighbourSlots[slot][n] = pair.Key;
                    neighbourWeights[slot][n] = pair.Value / sum;
                    n++;
                }
            }

            // まず ω = 1.8 で解く。収まらない（上限に届く・NaN・変化が増え続ける）ときは、最初の値から ω = 1.0 で解き直す。
            var sweeps = 0;
            var omega = Omega;
            var converged = Solve(Omega, unknowns, neighbourSlots, neighbourWeights, xs, ys, out sweeps);
            if (!converged)
            {
                foreach (var slot in unknowns) xs[slot] = ys[slot] = 0.0;
                omega = FallbackOmega;
                converged = Solve(FallbackOmega, unknowns, neighbourSlots, neighbourWeights, xs, ys, out sweeps);
            }

            if (!converged)
            {
                reason = NotConvergedReason;
                return false;
            }

            // 持つ座標（float）で、全三角形の符号付き面積が正か確かめる。
            var coordinates = new Dictionary<int, Vector2>(order.Count);
            for (var slot = 0; slot < order.Count; slot++) coordinates[order[slot]] = new Vector2((float)xs[slot], (float)ys[slot]);
            var cornerCoordinates = new Vector2[regionTriangles.Length * 3];
            for (var k = 0; k < regionTriangles.Length; k++)
            {
                for (var i = 0; i < 3; i++) cornerCoordinates[k * 3 + i] = coordinates[weldOf[triangles[regionTriangles[k] * 3 + i]]];
                Vector2 a = cornerCoordinates[k * 3], b = cornerCoordinates[k * 3 + 1], c = cornerCoordinates[k * 3 + 2];
                var area = ((double)b.x - a.x) * ((double)c.y - a.y) - ((double)b.y - a.y) * ((double)c.x - a.x);
                if (!(area > 0.0))
                {
                    reason = FlippedReason;
                    return false;
                }
            }

            disc = new FBXUVBodyRegionDisc(regionTriangles, weldOf, coordinates, centreGroup, rim, sweeps, omega, cornerCoordinates);
            return true;
        }

        // SOR。収まれば true。上限・NaN・変化が増え続ける（発散）ときは false。
        private static bool Solve(double omega, List<int> unknowns, int[][] neighbourSlots, double[][] neighbourWeights, double[] xs, double[] ys,
            out int sweeps)
        {
            sweeps = 0;
            var previous = double.MaxValue;
            var growing = 0;
            while (sweeps < MaximumSweeps)
            {
                sweeps++;
                var change = 0.0;
                foreach (var slot in unknowns)
                {
                    double sx = 0.0, sy = 0.0;
                    var ids = neighbourSlots[slot];
                    var weights = neighbourWeights[slot];
                    for (var n = 0; n < ids.Length; n++)
                    {
                        sx += weights[n] * xs[ids[n]];
                        sy += weights[n] * ys[ids[n]];
                    }

                    var x = (1.0 - omega) * xs[slot] + omega * sx;
                    var y = (1.0 - omega) * ys[slot] + omega * sy;
                    change = Math.Max(change, Math.Max(Math.Abs(x - xs[slot]), Math.Abs(y - ys[slot])));
                    xs[slot] = x;
                    ys[slot] = y;
                }

                // Math.Max は NaN を伝える。
                if (double.IsNaN(change) || double.IsInfinity(change)) return false;
                if (change < ChangeTolerance) return true;
                growing = change > previous ? growing + 1 : 0;
                if (growing >= GrowthLimit) return false;
                previous = change;
            }

            return false;
        }

        private static long EdgeKey(int a, int b)
        {
            return a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
        }

        private static void AddNeighbour(Dictionary<int, List<int>> neighbours, int group, int other)
        {
            if (!neighbours.TryGetValue(group, out var list)) neighbours[group] = list = new List<int>();
            list.Add(other);
        }

        // 範囲の束ごとの位置：範囲の三角形に使われるその束の頂点の位置の平均。
        private static Dictionary<int, double[]> GroupPositions(int[] regionTriangles, int[] triangles, Vector3[] positions, int[] weldOf)
        {
            var seen = new HashSet<int>();
            var sums = new Dictionary<int, double[]>();
            foreach (var t in regionTriangles)
            {
                for (var i = 0; i < 3; i++)
                {
                    var v = triangles[t * 3 + i];
                    if (!seen.Add(v)) continue;
                    if (!sums.TryGetValue(weldOf[v], out var sum)) sums[weldOf[v]] = sum = new double[4];
                    sum[0] += positions[v].x;
                    sum[1] += positions[v].y;
                    sum[2] += positions[v].z;
                    sum[3] += 1.0;
                }
            }

            var result = new Dictionary<int, double[]>(sums.Count);
            foreach (var pair in sums) result[pair.Key] = new[] { pair.Value[0] / pair.Value[3], pair.Value[1] / pair.Value[3], pair.Value[2] / pair.Value[3] };
            return result;
        }

        // c = 中心の束の Positions の平均、n = 中心の束の Normals の和の正規化。束の頂点は、三角形に使われる頂点だけを数える
        // （FBXUVBodyLandmarks.GroupMembers と同じ）。
        private static void CentreFrame(FBXUVBodyGeometry geometry, int[] weldOf, int centreGroup, out double[] centre, out double[] normal)
        {
            var positions = geometry.Positions;
            var normals = geometry.Normals;
            var referenced = new bool[positions.Length];
            foreach (var index in geometry.Triangles)
            {
                if (index >= 0 && index < referenced.Length) referenced[index] = true;
            }

            centre = new double[3];
            normal = new double[3];
            var count = 0;
            for (var v = 0; v < positions.Length; v++)
            {
                if (!referenced[v] || weldOf[v] != centreGroup) continue;
                centre[0] += positions[v].x;
                centre[1] += positions[v].y;
                centre[2] += positions[v].z;
                if (normals != null && v < normals.Length)
                {
                    normal[0] += normals[v].x;
                    normal[1] += normals[v].y;
                    normal[2] += normals[v].z;
                }

                count++;
            }

            centre = Scale(centre, 1.0 / count);
            var length = Math.Sqrt(Dot(normal, normal));
            normal = length > 0.0 ? Scale(normal, 1.0 / length) : new double[3];
        }

        private static double Dot(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];

        private static double[] Subtract(double[] a, double[] b) => new[] { a[0] - b[0], a[1] - b[1], a[2] - b[2] };

        private static double[] Scale(double[] a, double s) => new[] { a[0] * s, a[1] * s, a[2] * s };

        // Vector3.Cross と同じ式。
        private static double[] Cross(double[] a, double[] b) =>
            new[] { a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0] };

        private static bool Barycentric(Vector2 p, Vector2 a, Vector2 b, Vector2 c, out double wa, out double wb, out double wc)
        {
            double abx = (double)b.x - a.x, aby = (double)b.y - a.y;
            double acx = (double)c.x - a.x, acy = (double)c.y - a.y;
            double apx = (double)p.x - a.x, apy = (double)p.y - a.y;
            var denominator = abx * acy - acx * aby;
            if (denominator == 0.0)
            {
                wa = wb = wc = 0.0;
                return false;
            }

            wb = (apx * acy - acx * apy) / denominator;
            wc = (abx * apy - apx * aby) / denominator;
            wa = 1.0 - wb - wc;
            return true;
        }

        // 円盤の [−1,1]² を一辺 size のマスに区切ったときの番号。
        private static int Cell(float value, int size) => Mathf.Clamp(Mathf.FloorToInt((value + 1f) * 0.5f * size), 0, size - 1);

        // 格子は最初に TryLocate が呼ばれたときに1回だけ作る（Parallel.For から呼ばれてもよい）。
        private LocateGrid Grid()
        {
            var current = grid;
            if (current != null) return current;
            lock (gridLock)
            {
                if (grid == null) grid = BuildGrid();
                return grid;
            }
        }

        // 一辺 ⌈√(三角形の数)⌉ の格子。三角形を外接する長方形のマスに登録する。
        private LocateGrid BuildGrid()
        {
            var size = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(Triangles.Length)));
            var builder = new FBXUVTriangleGrid(size);
            for (var k = 0; k < Triangles.Length; k++)
            {
                Vector2 a = corners[k * 3], b = corners[k * 3 + 1], c = corners[k * 3 + 2];
                var min = Vector2.Min(a, Vector2.Min(b, c));
                var max = Vector2.Max(a, Vector2.Max(b, c));
                builder.Add(k, Cell(min.x, size), Cell(max.x, size), Cell(min.y, size), Cell(max.y, size));
            }

            builder.Build(out var ranges, out var indices);
            return new LocateGrid(size, ranges, indices);
        }

        private sealed class LocateGrid
        {
            internal readonly int Size;
            internal readonly Vector2Int[] Ranges;
            // 範囲の三角形の位置 k（Triangles の添字）。
            internal readonly int[] Indices;

            internal LocateGrid(int size, Vector2Int[] ranges, int[] indices)
            {
                Size = size;
                Ranges = ranges;
                Indices = indices;
            }
        }
    }
}
