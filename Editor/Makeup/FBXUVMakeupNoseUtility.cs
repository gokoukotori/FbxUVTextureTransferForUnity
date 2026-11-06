using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace GokouKotori.FBXUVTextureTransfer.Editor
{
    internal static class FBXUVMakeupNoseUtility
    {
        internal const string NoseLandmarkName = "鼻先";
        internal const string SuggestionDescription = "選択した顔のUV境界から目・口を自動選定し、静止Meshの眼口間中央でRoot +Zに最も突出する頂点を鼻先候補にします。Root +Yが上、+Zが正面のモデル用です。表情・現在のポーズは使いません。候補位置を確認して手動調整してください。";

        internal static bool TrySuggest(FBXUVMakeupTransferLayer layer, out FBXUVMakeupLandmark landmark, out string error)
        {
            landmark = null;
            error = "メイクレイヤーがありません。";
            if (layer == null) return false;
            try
            {
                if (!TrySuggestSide(layer.sourceModelOrPrefab, layer.sourceRegion, out var source, out error))
                { error = "Source: " + error; return false; }
                if (!TrySuggestSide(layer.targetModelOrPrefab, layer.targetRegion, out var target, out error))
                { error = "Target: " + error; return false; }
                landmark = new FBXUVMakeupLandmark { name = NoseLandmarkName, sourceUv = source, targetUv = target };
                error = null;
                return true;
            }
            catch (Exception exception)
            {
                error = "鼻先候補のMeshを読み取れません。顔領域を再選択してください。 " + exception.Message;
                return false;
            }
        }

        internal static bool TryAdd(FBXUVMakeupTransferLayer layer, out int index, out string error)
        {
            index = -1;
            error = "メイクレイヤーがありません。";
            if (layer == null) return false;
            if (layer.landmarks != null && layer.landmarks.Count >= FBXUVMakeupWarp.MaxLandmarks)
            { error = "対応点の上限128点に達しています。"; return false; }
            if (layer.landmarks != null && layer.landmarks.Any(p => p != null && p.name == NoseLandmarkName))
            { error = "鼻先の対応点は追加済みです。既存の鼻先点を編集してください。"; return false; }
            if (!TrySuggest(layer, out var candidate, out error)) return false;
            if (layer.landmarks != null && layer.landmarks.Any(p => p != null &&
                ((p.sourceUv - candidate.sourceUv).sqrMagnitude <= 1e-12f || (p.targetUv - candidate.targetUv).sqrMagnitude <= 1e-12f)))
            { error = "鼻先候補が既存のSourceまたはTarget対応点と重複・近接しています。既存点を編集してください。"; return false; }
            Undo.RecordObject(layer, "鼻先のメイク対応点を追加");
            if (layer.landmarks == null) layer.landmarks = new List<FBXUVMakeupLandmark>();
            index = layer.landmarks.Count;
            layer.landmarks.Add(candidate);
            EditorUtility.SetDirty(layer);
            PrefabUtility.RecordPrefabInstancePropertyModifications(layer);
            error = null;
            return true;
        }

        private static bool TrySuggestSide(GameObject reference, FBXUVTransferRegion region, out Vector2 tipUv, out string error)
        {
            tipUv = default;
            if (!FBXUVModelPrefabReferenceUtility.TryResolveRoot(reference, out var root, out error)) return false;
            error = "顔RegionのMeshまたはhashが無効です。顔領域を再選択してください。";
            if (region == null || !FBXUVMeshUtility.IsMeshHashCurrent(region) || region.triangles == null || region.triangles.Count == 0) return false;
            var renderers = root.GetComponentsInChildren<Renderer>(true).Where(r =>
                (r is SkinnedMeshRenderer skin ? skin.sharedMesh : r.GetComponent<MeshFilter>()?.sharedMesh) == region.mesh).ToArray();
            if (renderers.Length != 1)
            { error = "顔MeshのRendererを一意に特定できません。Root内で同じMeshを複数使用している場合は鼻先を手動指定してください。"; return false; }
            if (!FBXUVMakeupLandmarkUtility.TryAnalyze(region, out var analysis, out error)) return false;
            // TryAnalyze retains only independent, closed degree-two boundary components.
            // Defects elsewhere in the face must not reject usable eye/mouth loops.
            if (!FBXUVMakeupLandmarkUtility.TrySuggestLoops(analysis, out var loops, out error))
            { error = "鼻先候補には目・口境界の自動選定が必要です。" + error; return false; }

            var mesh = region.mesh;
            var indices = mesh.GetTriangles(region.subMeshIndex);
            var vertices = mesh.vertices;
            var uvs = new List<Vector2>(); mesh.GetUVs(region.uvChannel, uvs);
            var selected = new HashSet<int>(); var selectedTriangles = new HashSet<int>();
            foreach (var triangle in region.triangles)
            {
                if (triangle == null || triangle.index < 0 || triangle.index >= indices.Length / 3 || !selectedTriangles.Add(triangle.index))
                { error = "顔Regionの三角形indexが無効です。顔領域を再選択してください。"; return false; }
                for (var i = 0; i < 3; i++)
                {
                    var id = indices[triangle.index * 3 + i];
                    if (id < 0 || id >= vertices.Length || id >= uvs.Count || !Unit(uvs[id]) ||
                        (uvs[id] - triangle.GetPoint(i)).sqrMagnitude > 1e-12f)
                    { error = "顔RegionのUVが現在のMeshと一致しません。顔領域を再選択してください。"; return false; }
                    selected.Add(id);
                }
            }
            var ids = selected.OrderBy(i => i).ToArray();
            var transform = root.transform.worldToLocalMatrix * renderers[0].transform.localToWorldMatrix;
            var positions = new Vector3[vertices.Length];
            foreach (var id in ids)
            {
                positions[id] = transform.MultiplyPoint3x4(vertices[id]);
                if (!Finite(positions[id])) { error = "顔MeshのRoot座標が有限ではありません。"; return false; }
            }
            if (!TryCentroid(analysis.ClosedLoops[loops.mouth], ids, uvs, positions, out var mouth, out error)
                || !TryCentroid(analysis.ClosedLoops[loops.eyeLeftUv], ids, uvs, positions, out var left, out error)
                || !TryCentroid(analysis.ClosedLoops[loops.eyeRightUv], ids, uvs, positions, out var right, out error)) return false;
            var eye = (left + right) * .5f;
            var width = Mathf.Abs(left.x - right.x); var height = eye.y - mouth.y;
            if (width <= 1e-6f || height < width * .1f || height > width * 2f
                || Mathf.Abs(left.y - right.y) > width * .2f || Mathf.Abs(left.z - right.z) > width * .2f
                || Mathf.Abs(mouth.x - eye.x) > width * .25f)
            { error = "Root +Yが上・+Zが正面の眼口配置を確認できません。回転した顔や非対応形状は鼻先を手動指定してください。"; return false; }
            var candidates = ids.Where(id => Mathf.Abs((positions[id].x - eye.x) / width) <= .15f
                && (positions[id].y - mouth.y) / height >= .15f && (positions[id].y - mouth.y) / height <= .85f).ToArray();
            if (candidates.Length == 0)
            { error = "眼口間の中央領域に鼻先候補の頂点がありません。"; return false; }
            var tip = candidates.OrderByDescending(id => positions[id].z).ThenBy(id => id).First();
            var tipPosition = positions[tip];
            var yFraction = (tipPosition.y - mouth.y) / height;
            if (tipPosition.z <= Mathf.Max(eye.z, mouth.z) + width * .005f
                || Mathf.Abs((tipPosition.x - eye.x) / width) >= .149f || yFraction <= .151f || yFraction >= .849f)
            { error = "中央領域内の前方に突出した鼻先を確認できません。平坦な顔・逆向き・領域端の候補は手動指定してください。"; return false; }
            foreach (var id in ids)
            {
                // A seam at the same spatial tip cannot select a unique makeup UV.
                var samePosition = (positions[id] - tipPosition).sqrMagnitude <= 1e-12f;
                var tiedCandidate = candidates.Contains(id) && Mathf.Abs(positions[id].z - tipPosition.z) <= 1e-7f;
                if ((samePosition || tiedCandidate) && (uvs[id] - uvs[tip]).sqrMagnitude > 1e-12f)
                { error = "鼻先のUVがseamまたは同率の複数候補で一意ではありません。鼻先を手動指定してください。"; return false; }
            }
            tipUv = uvs[tip]; error = null; return true;
        }

        private static bool TryCentroid(List<Vector2> loop, int[] ids, List<Vector2> uvs, Vector3[] positions,
            out Vector3 center, out string error)
        {
            center = default;
            error = "目・口境界のUVと静止Mesh頂点を対応付けられません。";
            var points = new List<Vector3>();
            foreach (var uv in loop)
            {
                var closest = -1; var distance = float.PositiveInfinity;
                foreach (var id in ids)
                {
                    var d = (uvs[id] - uv).sqrMagnitude;
                    if (d < distance) { distance = d; closest = id; }
                }
                if (closest < 0 || distance > 4e-12f) return false;
                foreach (var id in ids)
                    if ((uvs[id] - uv).sqrMagnitude <= 4e-12f && (positions[id] - positions[closest]).sqrMagnitude > 1e-10f)
                    { error = "目・口境界の同じUVに異なる3D頂点があり、鼻先の基準が曖昧です。"; return false; }
                points.Add(positions[closest]);
            }
            double weight = 0, x = 0, y = 0, z = 0;
            for (var i = 0; i < points.Count; i++)
            {
                var a = points[i]; var b = points[(i + 1) % points.Count];
                var length = Vector3.Distance(a, b);
                weight += length; x += (a.x + (double)b.x) * .5d * length;
                y += (a.y + (double)b.y) * .5d * length; z += (a.z + (double)b.z) * .5d * length;
            }
            if (weight <= 1e-12) return false;
            center = new Vector3((float)(x / weight), (float)(y / weight), (float)(z / weight));
            error = null; return true;
        }

        private static bool Unit(Vector2 p) => Finite(p.x) && Finite(p.y) && p.x >= 0 && p.x <= 1 && p.y >= 0 && p.y <= 1;
        private static bool Finite(Vector3 p) => Finite(p.x) && Finite(p.y) && Finite(p.z);
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
