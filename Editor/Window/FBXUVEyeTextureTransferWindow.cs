using System;
using System.Collections.Generic;
using System.Linq;
using GokouKotori.FBXUVTextureTransfer.Editor.NDMF;
using UnityEditor;
using UnityEngine;

namespace GokouKotori.FBXUVTextureTransfer.Editor
{
    public sealed class FBXUVEyeTextureTransferWindow : EditorWindow
    {
        [SerializeField] private FBXUVEyeTextureTransferLayer layer;
        [SerializeField] private int selectedEye;
        [SerializeField] private int editingTab;
        private readonly FBXUVEyeManualEditor manualEditor = new FBXUVEyeManualEditor();
        private readonly Pane[] panes = { new Pane(), new Pane(), new Pane() };
        private Vector2 scroll;
        private string measurement, message;
        private string configurationState;
        private Vector2 windowScreenOrigin;
        [NonSerialized] internal readonly Rect[] imageRects = new Rect[3];
        [NonSerialized] internal Rect transferMethodRect, pupilModeRect;
        private sealed class Candidate
        {
            internal string Label, Path;
            internal Mesh Mesh;
            internal int Submesh;
        }
        private sealed class Pane
        {
            internal Candidate Candidate;
            internal int Uv, Island = -1;
            internal List<FBXUVIsland> Islands = new List<FBXUVIsland>();
            internal List<Candidate> Candidates;
            internal bool Initialized;
            internal string Error;
            internal float Zoom = 1;
            internal Vector2 Center = Vector2.one * .5f;
            internal GameObject Root;
            internal Texture TargetTexture;
        }

        public static void Open(FBXUVEyeTextureTransferLayer layer)
        {
            var window = GetWindow<FBXUVEyeTextureTransferWindow>("アイテクスチャ転送");
            window.layer = layer; window.Refresh(); window.Show(); window.Focus();
        }
        private void OnEnable()
        {
            minSize = new Vector2(960, 680);
            Undo.undoRedoPerformed += Refresh; EditorApplication.projectChanged += Refresh;
            EditorApplication.hierarchyChanged += InvalidateCandidates;
        }
        private void OnDisable()
        {
            manualEditor.Dispose();
            Undo.undoRedoPerformed -= Refresh; EditorApplication.projectChanged -= Refresh;
            EditorApplication.hierarchyChanged -= InvalidateCandidates;
        }
        private void OnLostFocus() => manualEditor.CancelDrag();
        private void InvalidateCandidates()
        {
            foreach (var pane in panes) pane.Candidates = null;
            Repaint();
        }
        private void Refresh()
        {
            manualEditor.Reset();
            foreach (var pane in panes) { pane.Initialized = false; pane.Candidates = null; pane.Error = null; }
            measurement = message = null; Repaint();
        }
        private void Changed()
        {
            EditorUtility.SetDirty(layer); PrefabUtility.RecordPrefabInstancePropertyModifications(layer);
            configurationState = EditorJsonUtility.ToJson(layer);
            measurement = message = null; Repaint();
        }

        private void OnGUI()
        {
            windowScreenOrigin = GUIUtility.GUIToScreenPoint(Vector2.zero);
            var nextLayer = (FBXUVEyeTextureTransferLayer)EditorGUILayout.ObjectField("Eye Transfer Layer", layer, typeof(FBXUVEyeTextureTransferLayer), true);
            if (nextLayer != layer) { layer = nextLayer; Refresh(); }
            if (layer == null) { EditorGUILayout.HelpBox("設定するEye Transfer Layerを指定してください。", MessageType.Info); return; }
            var currentState = EditorJsonUtility.ToJson(layer);
            if (configurationState != currentState) { measurement = message = null; configurationState = currentState; }
            scroll = EditorGUILayout.BeginScrollView(scroll);
            using (var serialized = new SerializedObject(layer))
            {
                serialized.Update(); FBXUVEyeTextureTransferLayerEditor.DrawConfiguration(serialized);
                if (serialized.ApplyModifiedProperties()) { InvalidateCandidates(); Changed(); }
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("目を追加", GUILayout.Width(100)))
                {
                    Undo.RecordObject(layer, "目の組み合わせを追加");
                    layer.eyes ??= new List<FBXUVEyeBinding>();
                    var number = layer.eyes.Count + 1; var name = "Eye " + number;
                    while (layer.eyes.Any(e => e?.name == name)) name = "Eye " + (++number);
                    layer.eyes.Add(new FBXUVEyeBinding { name = name }); selectedEye = layer.eyes.Count - 1;
                    Changed(); Refresh();
                }
                if (layer.eyes != null && layer.eyes.Count > 0)
                {
                    selectedEye = Mathf.Clamp(selectedEye, 0, layer.eyes.Count - 1);
                    var next = EditorGUILayout.Popup(selectedEye, layer.eyes.Select(e => e?.name ?? "未設定").ToArray());
                    if (next != selectedEye) { selectedEye = next; Refresh(); }
                    if (GUILayout.Button("この目を削除", GUILayout.Width(110)))
                    {
                        Undo.RecordObject(layer, "目の組み合わせを削除"); layer.eyes.RemoveAt(selectedEye);
                        selectedEye = Mathf.Max(0, selectedEye - 1); Changed(); Refresh();
                    }
                }
            }
            if (layer.eyes == null || layer.eyes.Count == 0) { EditorGUILayout.EndScrollView(); return; }
            var eye = layer.eyes[selectedEye];
            if (eye == null) { EditorGUILayout.HelpBox("無効な組み合わせです。削除して追加し直してください。", MessageType.Error); EditorGUILayout.EndScrollView(); return; }
            using (var serialized = new SerializedObject(layer))
            {
                var property = serialized.FindProperty("eyes").GetArrayElementAtIndex(selectedEye);
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.PropertyField(property.FindPropertyRelative("enabled"), new GUIContent("有効"));
                    EditorGUILayout.PropertyField(property.FindPropertyRelative("name"), new GUIContent("目の名前"));
                }
                EditorGUILayout.PropertyField(property.FindPropertyRelative("transferMethod"), new GUIContent("補正方式"));
                transferMethodRect = GUILayoutUtility.GetLastRect();
                EditorGUILayout.PropertyField(property.FindPropertyRelative("pupilMode"), new GUIContent("瞳孔の構成"));
                pupilModeRect = GUILayoutUtility.GetLastRect();
                if (serialized.ApplyModifiedProperties()) Changed();
            }
            var separatePupil = eye.pupilMode == FBXUVEyePupilMode.Separate;
            var nextTab = GUILayout.Toolbar(editingTab, new[] { "領域の選択", "手補正" });
            if (nextTab != editingTab) { manualEditor.Reset(); editingTab = nextTab; }
            if (editingTab == 0)
            {
                EditorGUILayout.HelpBox(separatePupil
                    ? "同じ側の目の「虹彩」「別パーツの瞳孔」「転送先」を選択します。画像クリックまたは「このアイランドを保存」で確定。スクロールで拡大、中ボタンで移動。"
                    : "「虹彩」と「転送先」を選択します。画像に描かれた瞳孔も含めて一体で転送します。瞳孔のない画像にも使用できます。瞳孔だけの位置・大きさ補正は行いません。", MessageType.Info);
                if (eye.transferMethod == FBXUVEyeTransferMethod.Topology)
                    EditorGUILayout.HelpBox("面のつながりと形状から内部の対応を計算します。画像一体型では、両側の辺が一点に集まる頂点を検出できる場合、その点を瞳孔中心として合わせます。別パーツの瞳孔に対応する穴がある場合は、虹彩の内周も追従します。模様が変形するため、従来型と比較して選んでください。", MessageType.Info);
                using (new EditorGUILayout.HorizontalScope())
                {
                    DrawPane(0, "転送元・虹彩", eye.iris);
                    if (separatePupil) DrawPane(1, "転送元・瞳孔", eye.pupil);
                    else imageRects[1] = default;
                    DrawPane(2, "転送先・目", eye.target);
                }
            }
            else
            {
                Array.Clear(imageRects, 0, imageRects.Length);
                manualEditor.Draw(layer, eye, Mathf.Max(280, (position.width - 65) / 2), Changed, Repaint);
            }
            if (separatePupil)
            using (var serialized = new SerializedObject(layer))
            {
                var property = serialized.FindProperty("eyes").GetArrayElementAtIndex(selectedEye);
                EditorGUILayout.LabelField("自動配置からの調整", EditorStyles.boldLabel);
                EditorGUILayout.PropertyField(property.FindPropertyRelative("pupilOffset"), new GUIContent("瞳孔の位置補正", "転送先の目の幅・高さを1とします。+Xはアバターの左、+Yは上です。"));
                EditorGUILayout.PropertyField(property.FindPropertyRelative("pupilScale"), new GUIContent("瞳孔の大きさ倍率", "1で自動計測値。縦横比を保って拡大・縮小します。"));
                EditorGUILayout.PropertyField(property.FindPropertyRelative("pupilAspect"), new GUIContent("瞳孔の縦横比倍率", "1で転送元の縦横比。面積を保ちながら幅÷高さを補正します。"));
                if (serialized.ApplyModifiedProperties()) Changed();
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("自動計測を確認")) Measure(eye);
                if (separatePupil && GUILayout.Button("補正をリセット"))
                {
                    Undo.RecordObject(layer, "瞳孔の補正をリセット"); eye.pupilOffset = Vector2.zero; eye.pupilScale = eye.pupilAspect = 1; Changed(); Measure(eye);
                }
                if (GUILayout.Button("設定を検証"))
                {
                    var issues = FBXUVEyeTransferValidation.ValidateLayer(layer);
                    message = issues.Count == 0 ? "設定は有効です。TTTプレビューで転写結果を確認してください。" : string.Join("\n", issues.Select(i => i.Message));
                }
            }
            if (measurement != null) EditorGUILayout.HelpBox(measurement, MessageType.Info);
            if (message != null) EditorGUILayout.HelpBox(message, MessageType.Info);
            EditorGUILayout.HelpBox("標準Meshから自動計算します。別パーツの瞳孔は縦横比と虹彩に対する投影面積比を保ちます。BlendShape・ポーズ・転送元Shaderの色やUV変形は計測に含めません。", MessageType.None);
            EditorGUILayout.EndScrollView();
        }

        private void Measure(FBXUVEyeBinding eye)
        {
            try
            {
                var mapping = FBXUVEyeMapping.Create(layer.sourceModelOrPrefab, layer.targetModelOrPrefab, eye, new FBXUVMeshAnalysisCache());
                if (!mapping.HasPupil)
                {
                    var targetCenter = mapping.TargetPupilCenter * .5f + Vector2.one * .5f;
                    measurement = mapping.HasTopologyPupilCenter
                        ? $"辺の収束点を瞳孔中心として対応付けました（各目の外接矩形内で0〜1）。\n転送元: ({mapping.MeasuredCenter.x:F4}, {mapping.MeasuredCenter.y:F4}) → 転送先: ({targetCenter.x:F4}, {targetCenter.y:F4})"
                        : eye.transferMethod == FBXUVEyeTransferMethod.Topology
                            ? "両側で一意の収束点を検出できないため、外周と面の形状から対応付けています。瞳孔中心は固定していません。"
                            : "画像一体型の設定は有効です。瞳孔の個別計測は行いません。";
                    return;
                }
                measurement = $"転送元の瞳孔中心: ({mapping.MeasuredCenter.x:F4}, {mapping.MeasuredCenter.y:F4})\n"
                    + $"虹彩に対する幅・高さ: {mapping.MeasuredSize.x:P2} / {mapping.MeasuredSize.y:P2}　縦横比（幅÷高さ）: {mapping.MeasuredAspect:F4}";
            }
            catch (Exception e) { measurement = e.Message; }
        }

        private void DrawPane(int index, string label, FBXUVEyeRegion saved)
        {
            var pane = panes[index]; var isTarget = index == 2;
            var reference = isTarget ? layer.targetModelOrPrefab : layer.sourceModelOrPrefab;
            FBXUVModelPrefabReferenceUtility.TryResolveRoot(reference, out var root, out _);
            Texture background = isTarget ? TargetTexture() : layer.sourceTexture;
            if (pane.Root != root || pane.TargetTexture != (isTarget ? background : null))
            { pane.Candidates = null; pane.Root = root; pane.TargetTexture = isTarget ? background : null; }
            pane.Candidates ??= Collect(root, isTarget ? background : null, isTarget);
            if (!pane.Initialized)
            {
                pane.Candidate = pane.Candidates.Find(c => c.Mesh == saved?.region?.mesh && c.Path == saved.rendererPath && c.Submesh == saved.region.subMeshIndex);
                if (pane.Candidate == null && saved?.region?.mesh == null && pane.Candidates.Count == 1) pane.Candidate = pane.Candidates[0];
                pane.Uv = saved?.region?.uvChannel ?? 0; pane.Island = saved?.region?.islandId ?? -1;
                pane.Initialized = true; Extract(pane);
            }
            using (new EditorGUILayout.VerticalScope(GUILayout.Width(Mathf.Max(280, (position.width - 55) / 3))))
            {
                EditorGUILayout.LabelField(label, EditorStyles.boldLabel);
                var selected = pane.Candidates.FindIndex(c => Same(c, pane.Candidate));
                var labels = new[] { "候補を選択…" }.Concat(pane.Candidates.Select(c => c.Label)).ToArray();
                var next = EditorGUILayout.Popup("Renderer / Submesh", selected + 1, labels) - 1;
                if (next != selected)
                { pane.Candidate = next < 0 ? null : pane.Candidates[next]; pane.Island = -1; Extract(pane); }
                var uv = EditorGUILayout.IntSlider("UVチャンネル", pane.Uv, 0, 7);
                if (uv != pane.Uv) { pane.Uv = uv; pane.Island = -1; Extract(pane); }
                using (new EditorGUI.DisabledScope(selected < 0 || pane.Islands.Count == 0))
                {
                    var islandIndex = pane.Islands.FindIndex(i => i.id == pane.Island);
                    var choices = new[] { "アイランドを選択…" }.Concat(pane.Islands.Select(i => $"{i.id} ({i.triangles.Count} triangles)")).ToArray();
                    var nextIsland = EditorGUILayout.Popup("候補アイランド", islandIndex + 1, choices) - 1;
                    if (nextIsland != islandIndex) pane.Island = nextIsland < 0 ? -1 : pane.Islands[nextIsland].id;
                }
                var size = Mathf.Max(260, (position.width - 65) / 3);
                var rect = GUILayoutUtility.GetRect(size, size, GUILayout.ExpandWidth(false));
                if (Event.current.type == EventType.Repaint)
                    imageRects[index] = new Rect(GUIUtility.GUIToScreenPoint(rect.position) - windowScreenOrigin, rect.size);
                GUI.BeginGroup(rect);
                var local = new Rect(0, 0, size, size);
                EditorGUI.DrawRect(local, new Color(.12f, .12f, .12f));
                var image = new Rect(size * .5f - pane.Center.x * size * pane.Zoom,
                    size * .5f - (1 - pane.Center.y) * size * pane.Zoom, size * pane.Zoom, size * pane.Zoom);
                if (background != null) GUI.DrawTexture(image, background, ScaleMode.StretchToFill, true);
                if (Event.current.type == EventType.Repaint && selected >= 0)
                { Handles.BeginGUI(); FBXUVIslandPreview.Draw(image, pane.Islands, pane.Island); Handles.EndGUI(); }
                var e = Event.current;
                if (local.Contains(e.mousePosition))
                {
                    if (e.type == EventType.ScrollWheel)
                    {
                        pane.Zoom = Mathf.Clamp(pane.Zoom * Mathf.Exp(-e.delta.y * .12f), 1, 64); e.Use(); Repaint();
                    }
                    else if (e.type == EventType.MouseDrag && e.button == 2)
                    { pane.Center += new Vector2(-e.delta.x, e.delta.y) / (size * pane.Zoom); e.Use(); Repaint(); }
                    else if (e.type == EventType.MouseDown && e.button == 0 && selected >= 0)
                    {
                        var point = new Vector2((e.mousePosition.x - image.x) / image.width, 1 - (e.mousePosition.y - image.y) / image.height);
                        var island = FBXUVIslandExtractor.HitTest(pane.Islands, point);
                        if (island != null) { pane.Island = island.id; Save(index, pane); }
                        e.Use();
                    }
                }
                GUI.EndGroup();
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("全体")) { pane.Center = Vector2.one * .5f; pane.Zoom = 1; }
                    if (GUILayout.Button("候補を拡大"))
                    {
                        var island = pane.Islands.Find(i => i.id == pane.Island);
                        if (island != null) { pane.Center = island.bounds.Center; pane.Zoom = Mathf.Clamp(.8f / Mathf.Max(island.bounds.Width, island.bounds.Height), 1, 64); }
                    }
                }
                using (new EditorGUI.DisabledScope(selected < 0 || pane.Island < 0))
                    if (GUILayout.Button("このアイランドを保存")) Save(index, pane);
                EditorGUILayout.LabelField("保存済み", saved?.region?.mesh == null ? "未選択" : $"{saved.rendererPath} / Submesh {saved.region.subMeshIndex} / UV {saved.region.uvChannel} / Island {saved.region.islandId}", EditorStyles.wordWrappedMiniLabel);
                if (pane.Candidates.Count == 0) EditorGUILayout.HelpBox(isTarget ? "CanvasのTarget Textureを使用する候補がありません。" : "Model / Prefabを指定してください。", MessageType.Warning);
                if (pane.Error != null) EditorGUILayout.HelpBox(pane.Error, MessageType.Warning);
            }
        }

        private static bool Same(Candidate a, Candidate b) => a != null && b != null && a.Mesh == b.Mesh && a.Path == b.Path && a.Submesh == b.Submesh;
        private static void Extract(Pane pane)
        {
            pane.Error = null; pane.Islands.Clear();
            if (pane.Candidate == null) return;
            try { pane.Islands = FBXUVIslandExtractor.Extract(pane.Candidate.Mesh, pane.Candidate.Submesh, pane.Uv); }
            catch (Exception e) { pane.Error = e.Message; }
        }
        private Texture TargetTexture() => FBXUVCanvasHierarchyUtility.TryFindCanvas(layer, out var canvas, out _, out _) ? canvas.TargetTexture?.SelectTexture : null;

        private static List<Candidate> Collect(GameObject root, Texture texture, bool target)
        {
            var result = new List<Candidate>();
            if (root == null || (target && texture == null)) return result;
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                var mesh = FBXUVEyeGeometry.MeshOf(renderer); if (mesh == null) continue;
                var path = FBXUVEyeGeometry.Path(root.transform, renderer.transform);
                for (var sub = 0; sub < mesh.subMeshCount; sub++)
                {
                    if (target && !FBXUVEyeTransferValidation.MatchesSelectedRenderer(root, texture,
                            new FBXUVEyeRegion { rendererPath = path, region = new FBXUVTransferRegion { mesh = mesh, subMeshIndex = sub } })) continue;
                    result.Add(new Candidate { Mesh = mesh, Path = path, Submesh = sub, Label = (string.IsNullOrEmpty(path) ? root.name : path) + " / " + sub });
                }
            }
            return result;
        }
        private void Save(int index, Pane pane)
        {
            var island = pane.Islands.Find(i => i.id == pane.Island); if (island == null || pane.Candidate == null) return;
            Undo.RecordObject(layer, "目のUVアイランドを保存");
            var selection = new FBXUVEyeRegion
            {
                rendererPath = pane.Candidate.Path,
                region = new FBXUVTransferRegion { mesh = pane.Candidate.Mesh, subMeshIndex = pane.Candidate.Submesh,
                    uvChannel = pane.Uv, islandId = island.id, triangles = island.triangles.ToList(), bounds = island.bounds,
                    meshHash = FBXUVMeshUtility.ComputeMeshContentHash(pane.Candidate.Mesh, pane.Candidate.Submesh, pane.Uv) }
            };
            var eye = layer.eyes[selectedEye];
            if (index == 0) eye.iris = selection; else if (index == 1) eye.pupil = selection; else eye.target = selection;
            Changed(); Measure(eye);
        }
    }
}
