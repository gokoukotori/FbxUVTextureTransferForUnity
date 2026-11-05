using System.Collections.Generic;
using UnityEngine;

namespace GokouKotori.FBXUVTextureTransfer
{
    internal enum FBXUVBodyNippleStatus
    {
        Found,
        LeftMissing,
        RightMissing,
        Asymmetric,
    }

    internal sealed class FBXUVBodyNipples
    {
        internal FBXUVBodyNippleStatus Status = FBXUVBodyNippleStatus.LeftMissing;
        internal bool Found => Status == FBXUVBodyNippleStatus.Found;
        internal int Left = -1;
        internal int Right = -1;
        internal int LeftValence;
        internal int RightValence;
        internal int LeftRunnerUp;
        internal int RightRunnerUp;
        internal Vector3 LeftPosition;
        internal Vector3 RightPosition;
        internal Vector3 LeftNormal;
        internal Vector3 RightNormal;
    }

    // 対応マップを作ったときの乳首合わせの結果（spec §3.5）。
    internal sealed class FBXUVBodyLandmarkResult
    {
        internal bool Enabled;
        internal bool Used;
        // 使わなかった理由。使ったときは null。
        internal string Reason;
        // 両アバターで左右とも見つかったときだけ入る。それ以外は null。
        internal FBXUVBodyLandmarkSide Left, Right;
    }

    internal sealed class FBXUVBodyLandmarkSide
    {
        internal int SourceVertex, TargetVertex;
        // m 単位、サイズ係数で割った値。OffsetBefore は転送先、OffsetAfter は転送元のサイズ係数で割る。
        // OffsetAfter は転送先の乳首から転送元の表面が見つからなければ NaN。
        internal float OffsetBefore, OffsetAfter;
        internal Vector2[] TargetUvs;
    }

    // 胸で辺が扇状に集まる点（乳首）を左右ひとつずつ探す。
    internal static class FBXUVBodyLandmarks
    {
        // 表面に沿ったずれの上限と、不要とみなす値。どちらも転送先のサイズ係数を掛ける。
        internal const float MaxOffsetMeters = 0.025f;
        internal const float MinOffsetMeters = 0.0001f;

        internal const string OffReason = "オフになっています";
        internal const string TooFarReason = "ずれが 25 mm（身長 1 m 換算）を超えるため、検出の誤りとみなしました";
        internal const string NotNeededReason = "ずれがないため、合わせは不要でした";

        private const float MinimumValence = 8f;
        // 乳首の価数は、その側のほかの候補の最大価数の2倍以上（同じ値どうしの引き分けもここで落ちる）。
        private const int MinimumDominance = 2;
        private const float MinimumNormalZ = 0.2f;
        private const float MinimumOffsetFromCentre = 0.02f;
        private const float MirrorTolerance = 0.005f;

        internal static string MissingReason(bool source, bool left)
        {
            return (source ? "転送元" : "転送先") + "で" + (left ? "左" : "右") + "の乳首が見つかりません";
        }

        internal static string AsymmetricReason(bool source)
        {
            return (source ? "転送元" : "転送先") + "の左右の乳首が対称ではありません";
        }

        internal static FBXUVBodyNipples Detect(FBXUVBodyGeometry geometry)
        {
            var result = new FBXUVBodyNipples();
            if (geometry == null || geometry.Positions == null || geometry.Normals == null || geometry.Triangles == null
                || geometry.InfluenceBones == null || geometry.InfluenceWeights == null || geometry.Joints == null) return result;
            var vertexCount = geometry.Positions.Length;
            if (vertexCount == 0 || geometry.Normals.Length < vertexCount
                || geometry.InfluenceBones.Length < vertexCount * 4 || geometry.InfluenceWeights.Length < vertexCount * 4) return result;
            if (!geometry.Joints.TryGetValue((int)HumanBodyBones.Hips, out var hips)
                || !geometry.Joints.TryGetValue((int)HumanBodyBones.Spine, out var spine)
                || !geometry.Joints.TryGetValue((int)HumanBodyBones.Neck, out var neck)) return result;

            var weldOf = FBXUVBodyWeld.Groups(geometry, out var groupCount);
            var referenced = ReferencedVertices(geometry);
            var adjacency = FBXUVBodyWeld.Adjacency(geometry.Triangles, weldOf, groupCount);
            var minimumOffset = MinimumOffsetFromCentre * geometry.SizeFactor;

            // candidates[0] = left (-X side), candidates[1] = right.
            var candidates = new[] { new SortedSet<int>(), new SortedSet<int>() };
            for (var v = 0; v < vertexCount; v++)
            {
                if (!referenced[v]) continue;
                var position = geometry.Positions[v];
                if (geometry.Normals[v].z <= MinimumNormalZ) continue;
                if (!(position.y > spine.y && position.y < neck.y)) continue;
                var offset = position.x - hips.x;
                if (Mathf.Abs(offset) < minimumOffset) continue;
                var bone = FBXUVBodyCorrespondence.DominantBoneOfVertex(geometry, v);
                if (bone != (int)HumanBodyBones.Spine && bone != (int)HumanBodyBones.Chest && bone != (int)HumanBodyBones.UpperChest) continue;
                candidates[offset < 0f ? 0 : 1].Add(weldOf[v]);
            }

            var members = new List<int>[groupCount];
            for (var v = 0; v < vertexCount; v++)
            {
                if (!referenced[v]) continue;
                var group = weldOf[v];
                if (members[group] == null) members[group] = new List<int>();
                members[group].Add(v);
            }

            var sides = new[] { new Side(), new Side() };
            for (var s = 0; s < 2; s++)
            {
                var side = sides[s];
                var best = -1;
                var runnerUp = 0;
                foreach (var group in candidates[s])
                {
                    var valence = adjacency[group].Length;
                    if (best < 0 || valence > side.Valence)
                    {
                        if (best >= 0) runnerUp = Mathf.Max(runnerUp, side.Valence);
                        best = group;
                        side.Valence = valence;
                    }
                    else runnerUp = Mathf.Max(runnerUp, valence);
                }

                side.RunnerUp = runnerUp;
                if (best < 0 || side.Valence < MinimumValence || side.Valence < MinimumDominance * runnerUp) continue;
                var list = members[best];
                var sum = Vector3.zero;
                var normal = Vector3.zero;
                foreach (var v in list)
                {
                    sum += geometry.Positions[v];
                    normal += geometry.Normals[v];
                }

                side.Vertex = list[0];
                side.Position = sum / list.Count;
                side.Normal = normal.normalized;
            }

            result.Left = sides[0].Vertex;
            result.LeftValence = sides[0].Valence;
            result.LeftRunnerUp = sides[0].RunnerUp;
            result.LeftPosition = sides[0].Position;
            result.LeftNormal = sides[0].Normal;
            result.Right = sides[1].Vertex;
            result.RightValence = sides[1].Valence;
            result.RightRunnerUp = sides[1].RunnerUp;
            result.RightPosition = sides[1].Position;
            result.RightNormal = sides[1].Normal;

            if (result.Left < 0) result.Status = FBXUVBodyNippleStatus.LeftMissing;
            else if (result.Right < 0) result.Status = FBXUVBodyNippleStatus.RightMissing;
            else
            {
                var mirrored = new Vector3(2f * hips.x - result.RightPosition.x, result.RightPosition.y, result.RightPosition.z);
                var difference = result.LeftPosition - mirrored;
                var tolerance = MirrorTolerance * geometry.SizeFactor;
                var symmetric = Mathf.Abs(difference.x) <= tolerance && Mathf.Abs(difference.y) <= tolerance && Mathf.Abs(difference.z) <= tolerance;
                result.Status = symmetric ? FBXUVBodyNippleStatus.Found : FBXUVBodyNippleStatus.Asymmetric;
            }

            return result;
        }

        // 同じ束（位置が同じ）の頂点。昇順。
        internal static int[] GroupMembers(FBXUVBodyGeometry geometry, int vertex)
        {
            if (geometry == null || geometry.Positions == null || vertex < 0 || vertex >= geometry.Positions.Length) return new int[0];
            var weldOf = FBXUVBodyWeld.Groups(geometry, out _);
            var referenced = ReferencedVertices(geometry);
            var group = weldOf[vertex];
            var list = new List<int>();
            for (var v = 0; v < weldOf.Length; v++)
            {
                if (referenced[v] && weldOf[v] == group) list.Add(v);
            }

            return list.ToArray();
        }

        // 束の頂点の UV。重複を除き、頂点番号の順。
        internal static Vector2[] GroupUvs(FBXUVBodyGeometry geometry, int vertex)
        {
            var uvs = new List<Vector2>();
            if (geometry == null || geometry.Uvs == null) return uvs.ToArray();
            foreach (var v in GroupMembers(geometry, vertex))
            {
                if (v >= geometry.Uvs.Length) continue;
                var uv = geometry.Uvs[v];
                if (!uvs.Contains(uv)) uvs.Add(uv);
            }

            return uvs.ToArray();
        }

        // 三角形から参照されている頂点だけ true（束には身体の三角形の頂点だけを数える）。
        private static bool[] ReferencedVertices(FBXUVBodyGeometry geometry)
        {
            var referenced = new bool[geometry.Positions.Length];
            foreach (var index in geometry.Triangles)
            {
                if (index >= 0 && index < referenced.Length) referenced[index] = true;
            }

            return referenced;
        }

        private sealed class Side
        {
            internal int Vertex = -1;
            internal int Valence;
            internal int RunnerUp;
            internal Vector3 Position;
            internal Vector3 Normal;
        }
    }
}
