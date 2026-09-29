using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace GokouKotori.FBXUVTextureTransfer
{
    [Serializable]
    public sealed class FBXUVEyeManualPin
    {
        // In the target eye's fitted plane, whose width and height are both 2.
        public Vector2 origin;
        public Vector2 position;
        public float radius = .5f;
        public bool fixedPoint;
    }

    [Serializable]
    public sealed class FBXUVEyeManualSettings
    {
        public bool enabled;
        public string regionKey;
        public Mesh irisMesh, targetMesh, pupilMesh;
        public List<FBXUVEyeManualPin> pins = new List<FBXUVEyeManualPin>();

        public FBXUVEyeManualSettings Copy() => new FBXUVEyeManualSettings
        {
            enabled = enabled, regionKey = regionKey, irisMesh = irisMesh, targetMesh = targetMesh, pupilMesh = pupilMesh,
            pins = pins?.Select(p => p == null ? null : new FBXUVEyeManualPin
            { origin = p.origin, position = p.position, radius = p.radius, fixedPoint = p.fixedPoint }).ToList()
                ?? new List<FBXUVEyeManualPin>()
        };

        internal void BindRegions(FBXUVEyeBinding binding)
        {
            regionKey = FBXUVEyeManualWarp.RegionKey(binding);
            irisMesh = binding.iris?.region?.mesh; targetMesh = binding.target?.region?.mesh;
            pupilMesh = binding.pupilMode == FBXUVEyePupilMode.Separate ? binding.pupil?.region?.mesh : null;
        }
    }

    // Compact-support inverse warp. A global derivative bound below 1 makes I + u
    // injective, including between samples. No dependence on the avatar's tessellation.
    internal sealed class FBXUVEyeManualWarp
    {
        internal const int MaxPins = 32;
        internal readonly Vector4[] Data;
        internal readonly float DerivativeBound;
        private readonly FBXUVEyeMapping map;
        private readonly Vector2[] pupilContour;
        private readonly List<Vector2[]> holes = new List<Vector2[]>();
        private readonly Vector2[] targetPlane;
        private readonly Vector2[] targetParameters;
        private readonly Vector2[] irisParameters;

        internal FBXUVEyeManualWarp(FBXUVEyeMapping mapping)
        {
            map = mapping;
            FBXUVEyeTopology.ValidateManualTarget(map);
            targetPlane = map.Target.Positions.Select(p => (Vector2)map.TargetFrame.Project(p)).ToArray();
            targetParameters = map.TargetCoordinates?.Select(p => (Vector2)p).ToArray() ?? targetPlane;
            irisParameters = map.IrisCoordinates?.Select(p => (Vector2)p).ToArray()
                ?? map.Iris.Positions.Select(p => (Vector2)map.SourceFrame.Project(p)).ToArray();
            pupilContour = map.HasPupil ? map.Pupil.Loops[0].Select(p => map.TargetPupilCenter
                + Vector2.Scale((Vector2)map.SourceFrame.Project(p) - map.SourcePupilCenter, map.PupilScale)).ToArray() : null;
            // Conventional transfer may leave an iris hole larger than the placed pupil.
            // Keep those missing-surface boundaries fixed as well.
            foreach (var loop in map.Iris.Loops.Skip(1))
            {
                var contour = new List<Vector2>();
                foreach (var vertex in loop)
                {
                    var index = Array.IndexOf(map.Iris.Positions, vertex);
                    var parameter = map.SourceFrame.MapTo(map.TargetFrame, irisParameters[index]);
                    if (!Locate(parameter, targetParameters, targetPlane, null, out var point))
                        throw Error("虹彩の内周を転送先へ対応付けできません。");
                    contour.Add(point);
                }
                holes.Add(contour.ToArray());
            }
            Data = Array.Empty<Vector4>();
        }

        private FBXUVEyeManualWarp(FBXUVEyeMapping mapping, IReadOnlyList<FBXUVEyeManualPin> pins) : this(mapping)
        {
            if (pins.Count > MaxPins) throw Error($"ピンは{MaxPins}個までです。");
            Data = new Vector4[pins.Count * 2];
            var matrix = new double[pins.Count, pins.Count + 2];
            for (var i = 0; i < pins.Count; i++)
            {
                var pin = pins[i];
                if (pin == null || !Finite(pin.origin) || !Finite(pin.position) || !FBXUVEyeGeometry.Finite(pin.radius) || pin.radius <= 0)
                    throw Error($"ピン{i + 1}の位置または影響範囲が不正です。");
                if (!CanPlace(pin.origin) || !CanPlace(pin.position))
                    throw Error($"ピン{i + 1}は虹彩の内部に置いてください。外周と別パーツの瞳孔は固定です。");
                var radius = EffectiveRadius(pin.position, pin.radius);
                if (radius < 1e-4f) throw Error($"ピン{i + 1}が境界に近すぎます。");
                Data[i * 2] = new Vector4(pin.position.x, pin.position.y, radius, 0);
                var delta = pin.origin - pin.position;
                matrix[i, pins.Count] = delta.x;
                matrix[i, pins.Count + 1] = delta.y;
                for (var j = 0; j < i; j++)
                    if ((pin.position - pins[j].position).sqrMagnitude < 1e-8f)
                        throw Error("ピンが重複しています。位置を離してください。");
            }
            for (var i = 0; i < pins.Count; i++)
                for (var j = 0; j < pins.Count; j++)
                    matrix[i, j] = Kernel(Vector2.Distance(pins[i].position, pins[j].position) / Data[j * 2].z);
            Solve(matrix, pins.Count);
            double bound = 0;
            for (var i = 0; i < pins.Count; i++)
            {
                var coefficient = new Vector2((float)matrix[i, pins.Count], (float)matrix[i, pins.Count + 1]);
                if (!Finite(coefficient)) throw Error("補正を計算できません。ピンの配置を確認してください。");
                Data[i * 2 + 1] = coefficient;
                // max |d((1-r)^4(1+4r))/dr| = 135/64.
                bound += coefficient.magnitude * (135.0 / 64) / Data[i * 2].z;
            }
            DerivativeBound = (float)bound;
            if (bound >= .9) throw Error("反転を避けるため、この補正は確定できません。移動量を減らすか、影響範囲を広げてください。");
            for (var i = 0; i < pins.Count; i++)
                if (Vector2.Distance(Inverse(pins[i].position), pins[i].origin) > 1e-5f)
                    throw Error("ピンの対応を正確に計算できません。ピンを離してください。");
        }

        internal static FBXUVEyeManualWarp Create(FBXUVEyeMapping map, FBXUVEyeBinding binding)
        {
            var settings = binding.manual;
            if (settings == null || !settings.enabled || settings.pins == null || settings.pins.Count == 0) return null;
            if (settings.regionKey != RegionKey(binding) || settings.irisMesh != binding.iris?.region?.mesh
                || settings.targetMesh != binding.target?.region?.mesh
                || (binding.pupilMode == FBXUVEyePupilMode.Separate && settings.pupilMesh != binding.pupil?.region?.mesh))
                throw Error("領域・補正方式・瞳孔の構成が変更されています。手補正をリセットするか無効にしてください。");
            return new FBXUVEyeManualWarp(map, settings.pins);
        }

        internal static string RegionKey(FBXUVEyeBinding binding)
        {
            string Key(FBXUVEyeRegion selection)
            {
                var r = selection?.region;
                return r == null ? "" : selection.rendererPath + ":" + r.meshHash + ":" + r.subMeshIndex + ":" + r.uvChannel
                    + ":" + string.Join(",", r.triangles?.Select(t => t.index) ?? Enumerable.Empty<int>());
            }
            return binding.transferMethod + "|" + binding.pupilMode + "|" + Key(binding.iris) + "|" + Key(binding.target)
                + (binding.pupilMode == FBXUVEyePupilMode.Separate ? "|" + Key(binding.pupil) : "");
        }

        internal Vector2 Inverse(Vector2 position)
        {
            var result = position;
            for (var i = 0; i < Data.Length; i += 2)
                result += (Vector2)Data[i + 1] * Kernel(Vector2.Distance(position, Data[i]) / Data[i].z);
            return result;
        }

        internal Vector2 Forward(Vector2 origin)
        {
            var point = origin;
            for (var i = 0; i < 160; i++)
            {
                var delta = origin - Inverse(point);
                point += delta;
                if (delta.sqrMagnitude < 1e-14f) break;
            }
            return point;
        }

        internal bool CanPlace(Vector2 position) => Finite(position) && Inside(position, map.TargetFrame.Contour)
            && (pupilContour == null || !Inside(position, pupilContour))
            && !holes.Any(hole => Inside(position, hole))
            && TrySourceUv(position, out _);

        internal float EffectiveRadius(Vector2 position, float requested)
        {
            var distance = DistanceToBoundary(position, map.TargetFrame.Contour);
            if (pupilContour != null) distance = Mathf.Min(distance, DistanceToBoundary(position, pupilContour));
            foreach (var hole in holes) distance = Mathf.Min(distance, DistanceToBoundary(position, hole));
            return Mathf.Min(requested, distance * .98f);
        }

        internal bool TryTargetPoint(Vector2 uv, out Vector2 point) => Locate(uv, map.Target.Uvs, targetPlane, null, out point);
        internal bool TryTargetUv(Vector2 point, out Vector2 uv) => Locate(point, targetPlane, map.Target.Uvs, null, out uv);
        internal bool TryPointFromSourceUv(Vector2 uv, out Vector2 point)
        {
            point = default;
            if (!Locate(uv, map.Iris.Uvs, irisParameters, null, out var parameter)) return false;
            return Locate(map.SourceFrame.MapTo(map.TargetFrame, parameter), targetParameters, targetPlane, null, out point);
        }
        internal bool TrySourceUv(Vector2 point, out Vector2 uv)
        {
            uv = default;
            if (!Locate(point, targetPlane, targetParameters, null, out var parameter)) return false;
            var source = map.TargetFrame.MapTo(map.SourceFrame, parameter);
            // Use the same frontmost surface rule as EyeTransfer.shader.
            return Locate(source, irisParameters, map.Iris.Uvs,
                map.IrisCoordinates ?? map.Iris.Positions.Select(map.SourceFrame.Project).ToArray(), out uv);
        }

        private static bool Locate(Vector2 p, Vector2[] coordinates, Vector2[] values, Vector3[] depths, out Vector2 value)
        {
            value = default; var found = false; var best = float.NegativeInfinity;
            for (var i = 0; i < coordinates.Length; i += 3)
            {
                var a = coordinates[i]; var b = coordinates[i + 1]; var c = coordinates[i + 2];
                var den = FBXUVEyeFrame.Cross(b - a, c - a);
                if (Mathf.Abs(den) < 1e-10f) continue;
                var wb = FBXUVEyeFrame.Cross(p - a, c - a) / den;
                var wc = FBXUVEyeFrame.Cross(b - a, p - a) / den; var wa = 1 - wb - wc;
                if (Mathf.Min(wa, Mathf.Min(wb, wc)) < -1e-5f) continue;
                var depth = depths == null ? 0 : wa * depths[i].z + wb * depths[i + 1].z + wc * depths[i + 2].z;
                if (depth < best) continue;
                best = depth; value = wa * values[i] + wb * values[i + 1] + wc * values[i + 2]; found = true;
            }
            return found;
        }

        private static float Kernel(float r)
        {
            if (r >= 1) return 0;
            var a = 1 - r; return a * a * a * a * (1 + 4 * r);
        }

        private static float DistanceToBoundary(Vector2 p, Vector2[] contour)
        {
            var distance = float.PositiveInfinity;
            for (var i = 0; i < contour.Length; i++)
            {
                var a = contour[i]; var edge = contour[(i + 1) % contour.Length] - a;
                var t = edge.sqrMagnitude > 0 ? Mathf.Clamp01(Vector2.Dot(p - a, edge) / edge.sqrMagnitude) : 0;
                distance = Mathf.Min(distance, Vector2.Distance(p, a + edge * t));
            }
            return distance;
        }

        private static bool Inside(Vector2 p, Vector2[] contour)
        {
            var inside = false;
            for (int i = 0, j = contour.Length - 1; i < contour.Length; j = i++)
                if ((contour[i].y > p.y) != (contour[j].y > p.y)
                    && p.x < (contour[j].x - contour[i].x) * (p.y - contour[i].y) / (contour[j].y - contour[i].y) + contour[i].x)
                    inside = !inside;
            return inside;
        }

        private static void Solve(double[,] a, int n)
        {
            for (var col = 0; col < n; col++)
            {
                var pivot = col;
                for (var row = col + 1; row < n; row++) if (Math.Abs(a[row, col]) > Math.Abs(a[pivot, col])) pivot = row;
                if (Math.Abs(a[pivot, col]) < 1e-8) throw Error("ピンが近すぎます。位置または影響範囲を調整してください。");
                for (var j = col; j < n + 2; j++) { var v = a[col, j]; a[col, j] = a[pivot, j]; a[pivot, j] = v; }
                var d = a[col, col]; for (var j = col; j < n + 2; j++) a[col, j] /= d;
                for (var row = 0; row < n; row++)
                {
                    if (row == col) continue;
                    var factor = a[row, col];
                    for (var j = col; j < n + 2; j++) a[row, j] -= factor * a[col, j];
                }
            }
        }

        private static bool Finite(Vector2 v) => FBXUVEyeGeometry.Finite(v.x) && FBXUVEyeGeometry.Finite(v.y);
        private static ArgumentException Error(string message) => new ArgumentException("Eye手補正: " + message);
    }
}
