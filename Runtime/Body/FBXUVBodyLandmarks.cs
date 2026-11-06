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

    // 目印の頂点の出どころ（2026-11-06 spec §4.1）。Pole は辺が扇状に集まる点、ShapeRegion は乳首のシェイプキーの範囲。
    internal enum FBXUVBodyLandmarkKind
    {
        Pole,
        ShapeRegion,
    }

    // アバターひとつ分の目印の選び方（2026-11-06 spec §4.1）。
    internal sealed class FBXUVBodyLandmarkChoice
    {
        // 目印があれば Found。無ければ Detect の結果のまま（理由のため）。
        internal FBXUVBodyNipples Nipples;
        internal FBXUVBodyLandmarkKind Kind;
        // 大きさにも使える範囲。無ければ null。
        internal FBXUVBodyNippleRegions Region;
        // geometry.NippleShapeName。
        internal string ShapeName;
        // 範囲を使わなかった理由の後半（「転送元の」などは呼び出し側が付ける）。使ったとき・シェイプキーが無いときは null。
        internal string ShapeReason;
    }

    // 対応マップを作ったときの乳首合わせの結果（spec §3.5、2026-11-06 spec §7.3）。
    internal sealed class FBXUVBodyLandmarkResult
    {
        internal bool Enabled;
        internal bool Used;
        // 使わなかった理由。使ったときは null。
        internal string Reason;
        // 両アバターで左右とも見つかったときだけ入る。それ以外は null。
        internal FBXUVBodyLandmarkSide Left, Right;
        // 種類・名前・理由は Enabled のときだけ入る。
        internal FBXUVBodyLandmarkKind SourceKind, TargetKind;
        // 使ったシェイプキーの名前。なしは null。
        internal string SourceShape, TargetShape;
        // シェイプキーを自動で選んだか（geometry.NippleShapeAuto）。
        internal bool SourceShapeAuto, TargetShapeAuto;
        // 範囲を使わなかった理由（完全な文）。使ったとき・シェイプキーが無いときは null。
        internal string SourceShapeReason, TargetShapeReason;
        // 大きさの判定を通ったか（k が無視できる大きさでも true）。通らなければ ScaleReason に理由が入る。
        // 判定は両方の目印があり、ずれが上限内のときだけ行う。行わなかったときは false と null。
        internal bool ScaleUsed;
        internal string ScaleReason;
        // 範囲どうしで写したか（2026-11-06 範囲どうしの設計書 §5.3）。
        internal bool RegionTransferUsed;
        // 範囲どうしで写さなかった理由。診断の行を出すとき（オンで、両方に使える範囲 Choice.Region があるとき）だけ入る。
        // 写したとき・行を出さないときは null。
        internal string RegionTransferReason;
    }

    internal sealed class FBXUVBodyLandmarkSide
    {
        internal int SourceVertex, TargetVertex;
        // m 単位、サイズ係数で割った値。OffsetBefore は転送先、OffsetAfter は転送元のサイズ係数で割る。
        // OffsetAfter は転送先の乳首から転送元の表面が見つからなければ NaN。
        internal float OffsetBefore, OffsetAfter;
        internal Vector2[] TargetUvs;
        // 範囲の半径 ρ を、それぞれのアバターのサイズ係数で割った値。使える範囲が無ければ NaN。
        internal float SourceRadius = float.NaN, TargetRadius = float.NaN;
        // ρ_s′（骨格合わせ後の転送元の範囲の半径）を転送先のサイズ係数で割った値。TargetRadius との比が k。大きさを合わせなければ NaN。
        internal float SourceAlignedRadius = float.NaN;
        // k = ρ_t / ρ_s′。大きさを合わせなければ（ScaleUsed が false なら）NaN。
        internal float Scale = float.NaN;
        // 範囲どうしで写した画素の数（RegionTransferUsed が false なら 0）。
        internal int RegionTexels;
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
        internal const string ScaleOutOfRangeReason = "大きさの比が 0.7〜1.4 倍を外れるため";
        internal const string ScaleOverlapReason = "左右の範囲が重なるため";

        // 範囲どうしで写さなかった理由（2026-11-06 範囲どうしの設計書 §5.4）。
        internal const string RegionNotNeededReason = "合わせが不要だったため";
        internal const string RegionNotUsedReason = "乳首の位置合わせを使っていないため";
        internal const string RegionNoScaleReason = "大きさを合わせていないため";

        // 辺の集まる点と範囲の中心がこれ（× サイズ係数）より離れたら、その範囲は使わない（2026-11-06 spec §4.1）。
        private const float RegionPoleTolerance = 0.005f;
        // 辺の集まる点と範囲の中心の3D距離の上限（× サイズ係数）。法線方向のずれが大きい範囲も使わない。
        private const float RegionPoleDistance = 0.04f;

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

        // 「{転送元|転送先}の」＋ tail（NippleShapeIssue、範囲の Reason、Choose の ShapeReason の後半）。tail が null なら null。
        internal static string ShapeReason(bool source, string tail)
        {
            return tail == null ? null : (source ? "転送元" : "転送先") + "の" + tail;
        }

        internal static string ShapeNoRegionReason(bool source)
        {
            return (source ? "転送元" : "転送先") + "にシェイプキーの範囲がありません";
        }

        // detail は FBXUVBodyRegionDisc.TryBuild の reason（「穴があります」など）。
        internal static string RegionDiscReason(bool source, bool left, string detail)
        {
            return (source ? "転送元" : "転送先") + "の" + (left ? "左" : "右") + "の範囲が円盤にできません（" + detail + "）";
        }

        // 2026-11-06 spec §4.1: the pole when Detect finds both sides, otherwise the shape region's landmark vertices.
        // With both, the region is kept (for the size) only when its centre is within 5 mm × SizeFactor of the pole on
        // both sides, measured in the pole's tangent plane (the area-weighted centre sinks below a curved cap, more so
        // for a larger region); otherwise the pole stays and the region is dropped with a reason.
        internal static FBXUVBodyLandmarkChoice Choose(FBXUVBodyGeometry geometry)
        {
            var nipples = Detect(geometry);
            var regions = FBXUVBodyNippleShape.Extract(geometry);
            var choice = new FBXUVBodyLandmarkChoice
            {
                Nipples = nipples,
                Kind = FBXUVBodyLandmarkKind.Pole,
                ShapeName = geometry?.NippleShapeName,
            };
            if (!regions.Found)
            {
                choice.ShapeReason = regions.Reason;
                return choice;
            }

            if (nipples.Found)
            {
                var limit = RegionPoleTolerance * geometry.SizeFactor;
                var distanceLimit = RegionPoleDistance * geometry.SizeFactor;
                if (TangentialDistance(regions.Left.Centre, nipples.LeftPosition, nipples.LeftNormal) > limit
                    || TangentialDistance(regions.Right.Centre, nipples.RightPosition, nipples.RightNormal) > limit
                    || (regions.Left.Centre - nipples.LeftPosition).magnitude > distanceLimit
                    || (regions.Right.Centre - nipples.RightPosition).magnitude > distanceLimit)
                {
                    choice.ShapeReason = $"シェイプキー {choice.ShapeName} の範囲が乳首から離れているため、使いませんでした";
                    return choice;
                }

                choice.Region = regions;
                return choice;
            }

            choice.Kind = FBXUVBodyLandmarkKind.ShapeRegion;
            choice.Region = regions;
            choice.Nipples = new FBXUVBodyNipples
            {
                Status = FBXUVBodyNippleStatus.Found,
                Left = regions.Left.Vertex,
                Right = regions.Right.Vertex,
            };
            GroupFrame(geometry, regions.Left.Vertex, out choice.Nipples.LeftPosition, out choice.Nipples.LeftNormal);
            GroupFrame(geometry, regions.Right.Vertex, out choice.Nipples.RightPosition, out choice.Nipples.RightNormal);
            return choice;
        }

        // |d − (d·n) n| with d = point − origin: the distance in the plane through origin perpendicular to the unit normal n.
        private static float TangentialDistance(Vector3 point, Vector3 origin, Vector3 normal)
        {
            var d = point - origin;
            return (d - Vector3.Dot(d, normal) * normal).magnitude;
        }

        // The mean position and the normalised normal sum of the vertex's weld group (as Detect gives for a pole).
        private static void GroupFrame(FBXUVBodyGeometry geometry, int vertex, out Vector3 position, out Vector3 normal)
        {
            var members = GroupMembers(geometry, vertex);
            var sum = Vector3.zero;
            normal = Vector3.zero;
            foreach (var v in members)
            {
                sum += geometry.Positions[v];
                if (geometry.Normals != null && v < geometry.Normals.Length) normal += geometry.Normals[v];
            }

            position = members.Length == 0 ? Vector3.zero : sum / members.Length;
            normal = normal.normalized;
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
