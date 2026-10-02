using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace GokouKotori.FBXUVTextureTransfer
{
    public enum FBXUVEyeTransferMethod
    {
        [InspectorName("従来型")] Conventional = 0,
        [InspectorName("トポロジー型")] Topology = 1
    }

    public enum FBXUVEyePupilMode
    {
        [InspectorName("別パーツあり")] Separate = 0,
        [InspectorName("別パーツなし（画像一体型）")] Integrated = 1
    }

    [Serializable]
    public sealed class FBXUVEyeRegion
    {
        public string rendererPath;
        public FBXUVTransferRegion region = new FBXUVTransferRegion();
    }

    [Serializable]
    public sealed class FBXUVEyeBinding
    {
        public string name = "Eye";
        public bool enabled = true;
        public FBXUVEyeTransferMethod transferMethod;
        public FBXUVEyePupilMode pupilMode;
        public FBXUVEyeRegion iris = new FBXUVEyeRegion();
        public FBXUVEyeRegion pupil = new FBXUVEyeRegion();
        public FBXUVEyeRegion target = new FBXUVEyeRegion();
        // Fractions of the target iris width/height; size and aspect are independent coefficients.
        public Vector2 pupilOffset;
        public float pupilScale = 1;
        public float pupilAspect = 1;
        public FBXUVEyeManualSettings manual = new FBXUVEyeManualSettings();
    }

    // Geometry comes from sharedMesh, never BakeMesh, texture alpha, or a posed skeleton.
    internal sealed class FBXUVEyeGeometry
    {
        internal Vector3[] Positions;
        internal Vector2[] Uvs;
        internal int[] Triangles;
        internal List<Vector3[]> Loops;

        internal static Renderer ResolveRenderer(GameObject root, FBXUVEyeRegion selection)
        {
            if (root == null || selection?.region?.mesh == null || selection.rendererPath == null)
                throw new ArgumentException("Model / Prefab とUVアイランドを選択してください。");
            var matches = FBXUVRendererMeshUtility.Collect(root).Where(item =>
                Path(root.transform, item.Renderer.transform) == selection.rendererPath && item.Mesh == selection.region.mesh)
                .Select(item => item.Renderer).ToArray();
            if (matches.Length != 1)
                throw new ArgumentException("選択したRendererを一意に取得できません。UVアイランドを選択し直してください。");
            return matches[0];
        }

        internal static string Path(Transform root, Transform item)
        {
            var names = new Stack<string>();
            while (item != root && item != null) { names.Push(item.name); item = item.parent; }
            return string.Join("/", names);
        }

        internal static FBXUVEyeGeometry Read(GameObject root, FBXUVEyeRegion selection, FBXUVMeshAnalysisCache cache)
        {
            var renderer = ResolveRenderer(root, selection);
            var region = selection.region;
            if (!FBXUVMeshUtility.IsMeshHashCurrent(region, cache) || region.triangles == null || region.triangles.Count == 0)
                throw new ArgumentException("MeshまたはUVが変更されています。UVアイランドを選択し直してください。");
            var live = cache.Get(region.mesh, region.subMeshIndex, region.uvChannel).GetTriangles();
            var indices = region.mesh.GetTriangles(region.subMeshIndex);
            var vertices = region.mesh.vertices;
            var matrix = root.transform.worldToLocalMatrix * renderer.transform.localToWorldMatrix;
            var points = new List<Vector3>();
            var uvs = new List<Vector2>();
            var tris = new List<int>();
            var seen = new HashSet<int>();
            foreach (var triangle in region.triangles)
            {
                if (triangle == null || triangle.index < 0 || triangle.index >= live.Count || !seen.Add(triangle.index))
                    throw new ArgumentException("UVアイランドの三角形が無効です。");
                for (var k = 0; k < 3; k++)
                {
                    var uv = triangle.GetPoint(k);
                    if (!FBXUVMakeupWarp.IsUnitUv(uv) || (uv - live[triangle.index].GetPoint(k)).sqrMagnitude > 1e-12f)
                        throw new ArgumentException("保存されたUVがMeshと一致しません。アイランドを選択し直してください。");
                    var p = matrix.MultiplyPoint3x4(vertices[indices[triangle.index * 3 + k]]);
                    if (!Finite(p.x) || !Finite(p.y) || !Finite(p.z)) throw new ArgumentException("Meshの座標が不正です。");
                    tris.Add(points.Count); points.Add(p); uvs.Add(uv);
                }
            }
            var result = new FBXUVEyeGeometry { Positions = points.ToArray(), Uvs = uvs.ToArray(), Triangles = tris.ToArray() };
            result.Loops = Boundary(result.Positions);
            return result;
        }

        // Weld spatial seams, while keeping the original per-corner UVs for sampling.
        private static List<Vector3[]> Boundary(Vector3[] points)
        {
            var vertices = new List<Vector3>();
            var weld = new Dictionary<Vector3Int, int>();
            var ids = new int[points.Length];
            for (var i = 0; i < points.Length; i++)
            {
                var p = points[i];
                var key = new Vector3Int(Mathf.RoundToInt(p.x * 1000000), Mathf.RoundToInt(p.y * 1000000), Mathf.RoundToInt(p.z * 1000000));
                if (!weld.TryGetValue(key, out var id)) { id = vertices.Count; weld.Add(key, id); vertices.Add(p); }
                ids[i] = id;
            }
            var edges = new Dictionary<(int, int), int>();
            var connected = new Dictionary<int, List<int>>();
            for (var i = 0; i < ids.Length; i += 3)
            {
                for (var k = 0; k < 3; k++)
                {
                    var a = ids[i + k]; var b = ids[i + (k + 1) % 3];
                    if (a == b) throw new ArgumentException("選択領域に退化した三角形があります。");
                    var key = a < b ? (a, b) : (b, a);
                    edges.TryGetValue(key, out var count); edges[key] = count + 1;
                    if (!connected.TryGetValue(a, out var neighbours)) connected[a] = neighbours = new List<int>();
                    neighbours.Add(b); neighbours = connected.TryGetValue(b, out var back) ? back : (connected[b] = new List<int>());
                    neighbours.Add(a);
                }
            }
            var reached = new HashSet<int>(); var stack = new Stack<int>(); stack.Push(0);
            while (stack.Count > 0)
            {
                var v = stack.Pop(); if (!reached.Add(v)) continue;
                foreach (var next in connected[v]) stack.Push(next);
            }
            if (reached.Count != vertices.Count)
                throw new ArgumentException("同じUVに複数の立体領域が重なっています。片目ごとに分離したアイランドが必要です。");
            var boundary = new Dictionary<int, List<int>>();
            foreach (var edge in edges)
            {
                if (edge.Value > 2) throw new ArgumentException("選択領域に分岐した面があります。");
                if (edge.Value != 1) continue;
                var a = edge.Key.Item1; var b = edge.Key.Item2;
                if (!boundary.ContainsKey(a)) boundary[a] = new List<int>();
                if (!boundary.ContainsKey(b)) boundary[b] = new List<int>();
                boundary[a].Add(b); boundary[b].Add(a);
            }
            if (boundary.Count < 3 || boundary.Values.Any(v => v.Count != 2))
                throw new ArgumentException("閉じた目の輪郭を取得できません。");
            var remaining = new HashSet<int>(boundary.Keys); var loops = new List<Vector3[]>();
            while (remaining.Count > 0)
            {
                var first = remaining.Min(); var at = first; var previous = -1; var loop = new List<Vector3>();
                do
                {
                    if (!remaining.Remove(at)) throw new ArgumentException("目の輪郭が交差しています。");
                    loop.Add(vertices[at]); var neighbours = boundary[at];
                    var next = neighbours[0] == previous ? neighbours[1] : neighbours[0]; previous = at; at = next;
                } while (at != first);
                loops.Add(loop.ToArray());
            }
            return loops.OrderByDescending(loop => AreaVector(loop).sqrMagnitude).ToList();
        }

        private static Vector3 AreaVector(Vector3[] loop)
        {
            var area = Vector3.zero;
            for (var i = 0; i < loop.Length; i++) area += Vector3.Cross(loop[i] - loop[0], loop[(i + 1) % loop.Length] - loop[0]);
            return area;
        }

        internal static bool Finite(float f) => !float.IsNaN(f) && !float.IsInfinity(f);
    }

    internal sealed class FBXUVEyeFrame
    {
        internal Vector3 Origin, Right, Up, Normal;
        internal Vector2 Center, Half;
        internal Vector2[] Contour;
        internal float Area;

        internal Vector3 Project(Vector3 p)
        {
            p -= Origin;
            return new Vector3((Vector3.Dot(p, Right) - Center.x) / Half.x,
                (Vector3.Dot(p, Up) - Center.y) / Half.y, Vector3.Dot(p, Normal));
        }

        internal static FBXUVEyeFrame Create(FBXUVEyeGeometry geometry)
        {
            var loop = geometry.Loops[0];
            var frame = new FBXUVEyeFrame { Origin = loop.Aggregate(Vector3.zero, (v, p) => v + p) / loop.Length };
            frame.Normal = FitNormal(loop, frame.Origin);
            if (frame.Normal.z < 0) frame.Normal = -frame.Normal;
            if (frame.Normal.z < .1f) throw new ArgumentException("目の正面を特定できません。Model / Prefab の +Z が顔の正面になる配置が必要です。");
            frame.Right = Vector3.Cross(Vector3.up, frame.Normal).normalized;
            frame.Up = Vector3.Cross(frame.Normal, frame.Right);
            var projected = loop.Select(p => new Vector2(Vector3.Dot(p - frame.Origin, frame.Right), Vector3.Dot(p - frame.Origin, frame.Up))).ToArray();
            var min = projected.Aggregate(Vector2.Min); var max = projected.Aggregate(Vector2.Max);
            frame.Center = (min + max) * .5f; frame.Half = (max - min) * .5f;
            if (frame.Half.x < 1e-6f || frame.Half.y < 1e-6f) throw new ArgumentException("目の輪郭が小さすぎるか退化しています。");
            frame.Contour = projected.Select(p => new Vector2((p.x - frame.Center.x) / frame.Half.x, (p.y - frame.Center.y) / frame.Half.y)).ToArray();
            var signedArea = 0f;
            for (var i = 0; i < frame.Contour.Length; i++) signedArea += Cross(frame.Contour[i], frame.Contour[(i + 1) % frame.Contour.Length]);
            if (signedArea < 0) Array.Reverse(frame.Contour);
            frame.Area = Mathf.Abs(signedArea) * .5f * frame.Half.x * frame.Half.y;
            // A consistent polar order guarantees exactly one outer boundary intersection per ray.
            var totalAngle = 0f;
            for (var i = 0; i < frame.Contour.Length; i++)
            {
                var a = frame.Contour[i]; var b = frame.Contour[(i + 1) % frame.Contour.Length];
                var cross = Cross(a, b);
                if (cross <= 1e-8f) throw new ArgumentException("目の輪郭を中心から一方向に対応付けできません。別のアイランドを選択してください。");
                totalAngle += Mathf.Atan2(cross, Vector2.Dot(a, b));
            }
            if (Mathf.Abs(totalAngle - 2 * Mathf.PI) > .001f) throw new ArgumentException("目の輪郭が自己交差しています。");
            return frame;
        }

        // Small symmetric eigensolver: the least-variance axis is the fitted plane normal.
        private static Vector3 FitNormal(Vector3[] points, Vector3 origin)
        {
            var a = new double[3, 3]; var v = new double[3, 3];
            for (var i = 0; i < 3; i++) v[i, i] = 1;
            foreach (var p in points)
            {
                var d = p - origin;
                for (var i = 0; i < 3; i++) for (var j = 0; j < 3; j++) a[i, j] += (double)d[i] * d[j];
            }
            for (var iteration = 0; iteration < 32; iteration++)
            {
                var p = 0; var q = 1;
                for (var i = 0; i < 3; i++) for (var j = i + 1; j < 3; j++)
                    if (Math.Abs(a[i, j]) > Math.Abs(a[p, q])) { p = i; q = j; }
                if (Math.Abs(a[p, q]) < 1e-18) break;
                var angle = .5 * Math.Atan2(2 * a[p, q], a[q, q] - a[p, p]);
                var c = Math.Cos(angle); var s = Math.Sin(angle);
                var pp = a[p, p]; var qq = a[q, q]; var pq = a[p, q];
                for (var k = 0; k < 3; k++)
                {
                    if (k != p && k != q)
                    {
                        var kp = a[k, p]; var kq = a[k, q];
                        a[k, p] = a[p, k] = c * kp - s * kq;
                        a[k, q] = a[q, k] = s * kp + c * kq;
                    }
                    var vp = v[k, p]; var vq = v[k, q];
                    v[k, p] = c * vp - s * vq; v[k, q] = s * vp + c * vq;
                }
                a[p, p] = c * c * pp - 2 * s * c * pq + s * s * qq;
                a[q, q] = s * s * pp + 2 * s * c * pq + c * c * qq; a[p, q] = a[q, p] = 0;
            }
            var smallest = a[0, 0] < a[1, 1] ? 0 : 1; if (a[2, 2] < a[smallest, smallest]) smallest = 2;
            return new Vector3((float)v[0, smallest], (float)v[1, smallest], (float)v[2, smallest]).normalized;
        }

        internal static float Cross(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;
        internal float Radius(Vector2 p)
        {
            // Vector2.normalized rounds small nonzero vectors to zero; match the shader instead.
            var direction = p.sqrMagnitude < 1e-16f ? Vector2.right : p / Mathf.Sqrt(p.sqrMagnitude);
            var distance = float.PositiveInfinity;
            for (var i = 0; i < Contour.Length; i++)
            {
                var a = Contour[i]; var edge = Contour[(i + 1) % Contour.Length] - a;
                var denominator = Cross(direction, edge); if (Mathf.Abs(denominator) < 1e-8f) continue;
                var t = Cross(a, edge) / denominator; var u = Cross(a, direction) / denominator;
                if (t > 0 && u >= -1e-5f && u <= 1 + 1e-5f) distance = Mathf.Min(distance, t);
            }
            return distance;
        }

        internal Vector2 MapTo(FBXUVEyeFrame other, Vector2 p) => p * (other.Radius(p) / Radius(p));
    }

    internal sealed class FBXUVEyeMapping
    {
        internal FBXUVEyeGeometry Iris, Pupil, Target;
        internal FBXUVEyeFrame SourceFrame, TargetFrame;
        internal Vector2 SourcePupilCenter, TargetPupilCenter, PupilScale;
        internal Vector2 MeasuredCenter, MeasuredSize;
        internal float MeasuredAspect;
        internal Vector3[] IrisCoordinates, TargetCoordinates;
        internal FBXUVEyeManualWarp ManualWarp;
        internal bool HasPupil => Pupil != null;
        internal bool HasTopologyPupilCenter;

        internal static FBXUVEyeMapping Create(GameObject source, GameObject target, FBXUVEyeBinding binding, FBXUVMeshAnalysisCache cache)
        {
            if (binding == null) throw new ArgumentException("目の組み合わせが無効です。");
            if (!Enum.IsDefined(typeof(FBXUVEyeTransferMethod), binding.transferMethod)
                || !Enum.IsDefined(typeof(FBXUVEyePupilMode), binding.pupilMode))
                throw new ArgumentException("補正方式または瞳孔の構成が無効です。");
            var separatePupil = binding.pupilMode == FBXUVEyePupilMode.Separate;
            if (separatePupil && (!FBXUVEyeGeometry.Finite(binding.pupilScale) || binding.pupilScale <= 0
                || !FBXUVEyeGeometry.Finite(binding.pupilAspect) || binding.pupilAspect <= 0
                || !FBXUVEyeGeometry.Finite(binding.pupilOffset.x) || !FBXUVEyeGeometry.Finite(binding.pupilOffset.y)))
                throw new ArgumentException("瞳孔の倍率・縦横比には正の有限値、位置には有限値を指定してください。");
            var map = new FBXUVEyeMapping
            {
                Iris = FBXUVEyeGeometry.Read(source, binding.iris, cache),
                Pupil = separatePupil ? FBXUVEyeGeometry.Read(source, binding.pupil, cache) : null,
                Target = FBXUVEyeGeometry.Read(target, binding.target, cache)
            };
            if (separatePupil && binding.iris.rendererPath == binding.pupil.rendererPath && binding.iris.region.mesh == binding.pupil.region.mesh
                && binding.iris.region.subMeshIndex == binding.pupil.region.subMeshIndex
                && binding.iris.region.triangles.Select(t => t.index).Intersect(binding.pupil.region.triangles.Select(t => t.index)).Any())
                throw new ArgumentException("虹彩と瞳孔には別の面を選択してください。同じアイランドは使用できません。");
            if (map.Target.Loops.Count != 1 || (separatePupil && map.Pupil.Loops.Count != 1))
                throw new ArgumentException("瞳孔と転送先には穴のないアイランドを指定してください。");
            map.SourceFrame = FBXUVEyeFrame.Create(map.Iris); map.TargetFrame = FBXUVEyeFrame.Create(map.Target);
            ValidateProjection(map.Iris, map.SourceFrame, "虹彩");
            if (separatePupil) ValidateProjection(map.Pupil, map.SourceFrame, "瞳孔");
            ValidateProjection(map.Target, map.TargetFrame, "転送先");
            if (!separatePupil)
            {
                map.PupilScale = Vector2.one;
                if (binding.transferMethod == FBXUVEyeTransferMethod.Topology) FBXUVEyeTopology.Apply(map, binding);
                map.ManualWarp = FBXUVEyeManualWarp.Create(map, binding);
                return map;
            }
            var projected = map.Pupil.Positions.Select(p => (Vector2)map.SourceFrame.Project(p)).ToArray();
            var min = projected.Aggregate(Vector2.Min); var max = projected.Aggregate(Vector2.Max);
            map.SourcePupilCenter = (min + max) * .5f;
            if (map.SourcePupilCenter.magnitude > map.SourceFrame.Radius(map.SourcePupilCenter))
                throw new ArgumentException("瞳孔の中心が虹彩の外側にあります。同じ目のアイランドを選択してください。");
            map.MeasuredCenter = map.SourcePupilCenter * .5f + Vector2.one * .5f;
            map.MeasuredSize = (max - min) * .5f;
            map.MeasuredAspect = (max.x - min.x) * map.SourceFrame.Half.x / ((max.y - min.y) * map.SourceFrame.Half.y);
            var scale = Mathf.Sqrt(map.TargetFrame.Area / map.SourceFrame.Area) * binding.pupilScale;
            // sqrt(aspect) preserves area when the user changes only the aspect coefficient.
            var aspect = Mathf.Sqrt(binding.pupilAspect);
            map.PupilScale = new Vector2(scale * aspect * map.SourceFrame.Half.x / map.TargetFrame.Half.x,
                scale / aspect * map.SourceFrame.Half.y / map.TargetFrame.Half.y);
            map.TargetPupilCenter = map.SourceFrame.MapTo(map.TargetFrame, map.SourcePupilCenter) + binding.pupilOffset * 2;
            if (!FBXUVEyeGeometry.Finite(map.MeasuredAspect) || map.MeasuredAspect <= 0)
                throw new ArgumentException("瞳孔の縦横比を計測できません。");
            if (!FBXUVEyeGeometry.Finite(map.PupilScale.x) || !FBXUVEyeGeometry.Finite(map.PupilScale.y)
                || map.PupilScale.x <= 0 || map.PupilScale.y <= 0
                || !FBXUVEyeGeometry.Finite(map.TargetPupilCenter.x) || !FBXUVEyeGeometry.Finite(map.TargetPupilCenter.y))
                throw new ArgumentException("瞳孔の補正値が計算できる範囲を超えています。");
            if (binding.transferMethod == FBXUVEyeTransferMethod.Topology) FBXUVEyeTopology.Apply(map, binding);
            map.ManualWarp = FBXUVEyeManualWarp.Create(map, binding);
            return map;
        }

        private static void ValidateProjection(FBXUVEyeGeometry geometry, FBXUVEyeFrame frame, string label)
        {
            for (var i = 0; i < geometry.Positions.Length; i += 3)
            {
                var a = frame.Project(geometry.Positions[i]); var b = frame.Project(geometry.Positions[i + 1]); var c = frame.Project(geometry.Positions[i + 2]);
                if (Mathf.Abs(FBXUVEyeFrame.Cross((Vector2)(b - a), (Vector2)(c - a))) < 1e-10f)
                    throw new ArgumentException(label + "の正面投影に退化した三角形があります。");
            }
        }
    }
}
