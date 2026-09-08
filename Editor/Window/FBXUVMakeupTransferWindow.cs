using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace GokouKotori.FBXUVTextureTransfer.Editor
{
    public sealed class FBXUVMakeupTransferWindow : EditorWindow
    {
        [SerializeField] private FBXUVMakeupTransferLayer layer;
        [SerializeField] private bool selectIsland = true;
        [SerializeField] private int selectedPoint = -1;
        [NonSerialized] internal Rect sourceImageRect;
        [NonSerialized] internal Rect targetImageRect;
        private Vector2 pointsScroll;
        private string message;
        private readonly Pane source = new Pane();
        private readonly Pane target = new Pane();
        private int dragControl;
        private int dragUndoGroup = -1;
        private GameObject cachedSourceModelOrPrefab;
        private GameObject cachedTargetModelOrPrefab;
        private Texture cachedTargetTexture;
        private bool refreshRequested;

        private readonly FBXUVMeshCandidateCache candidateCache = new FBXUVMeshCandidateCache();

        private sealed class Pane
        {
            internal Mesh mesh;
            internal int submesh;
            internal int uv;
            internal List<FBXUVIsland> islands = new List<FBXUVIsland>();
            internal string error;
            internal bool initialized;
            internal bool hasPendingRegionSelection;
            internal Mesh savedMesh;
            internal int savedSubmesh;
            internal int savedUv;
            internal int savedIslandId;
            internal string savedMeshHash;
            internal FBXUVTransferRegion analyzedRegion;
            internal FBXUVMakeupLandmarkUtility.BoundaryAnalysis boundaries;
            internal FBXUVMakeupLandmarkUtility.BoundarySelection boundarySelection = FBXUVMakeupLandmarkUtility.BoundarySelection.Empty;
            internal string boundaryError;
        }

        public static void Open(FBXUVMakeupTransferLayer layer)
        {
            var window = GetWindow<FBXUVMakeupTransferWindow>();
            window.titleContent = new GUIContent("メイク転送");
            window.minSize = new Vector2(900, 780);
            window.SetLayer(layer);
            window.Show();
            window.Focus();
        }

        internal static void RepaintFor(FBXUVMakeupTransferLayer layer)
        {
            if (layer == null) return;
            foreach (var window in Resources.FindObjectsOfTypeAll<FBXUVMakeupTransferWindow>())
                if (window.layer == layer) window.Repaint();
        }

        internal static void OpenPoint(FBXUVMakeupTransferLayer layer, int index)
        {
            Open(layer);
            var window = GetWindow<FBXUVMakeupTransferWindow>();
            window.selectedPoint = index;
            window.selectIsland = false;
        }

        internal bool TryAddNoseLandmark()
        {
            if (!FBXUVMakeupNoseUtility.TryAdd(layer, out var index, out var error))
            {
                message = error;
                return false;
            }
            selectedPoint = index;
            selectIsland = false;
            message = "鼻先の候補を追加しました。両方の画像で位置を確認し、必要なら移動してください。鼻先の追加は眼・口・頬にも影響するため、アバター表示も確認してください。";
            Repaint();
            return true;
        }

        private void OnEnable()
        {
            Undo.undoRedoPerformed += Refresh;
            EditorApplication.projectChanged += RequestRefresh;
            EditorApplication.hierarchyChanged += RequestRefresh;
        }

        private void OnDisable()
        {
            Undo.undoRedoPerformed -= Refresh;
            EditorApplication.projectChanged -= RequestRefresh;
            EditorApplication.hierarchyChanged -= RequestRefresh;
            EndDrag();
        }

        private void SetLayer(FBXUVMakeupTransferLayer next)
        {
            EndDrag();
            layer = next;
            selectedPoint = -1;
            message = null;
            pointsScroll = Vector2.zero;
            Refresh();
            SynchronizeConfiguration();
        }

        private void OnSelectionChange()
        {
            var selectedLayer = Selection.activeGameObject == null
                ? null : Selection.activeGameObject.GetComponent<FBXUVMakeupTransferLayer>();
            if (selectedLayer != null && selectedLayer != layer) SetLayer(selectedLayer);
        }

        private void OnInspectorUpdate()
        {
            if (SynchronizeConfiguration()) Repaint();
        }

        private void RequestRefresh()
        {
            candidateCache.Invalidate();
            refreshRequested = true;
            Repaint();
        }

        private void Refresh()
        {
            candidateCache.Invalidate();
            // Undo/Redo and layer changes restore saved selections; ordinary refreshes keep pending choices.
            EndDrag();
            source.initialized = target.initialized = false;
            source.hasPendingRegionSelection = target.hasPendingRegionSelection = false;
            source.analyzedRegion = target.analyzedRegion = null;
            source.boundaries = target.boundaries = null;
            refreshRequested = true;
            Repaint();
        }

        private bool SynchronizeConfiguration()
        {
            var sourceRoot = layer == null ? null : layer.sourceModelOrPrefab;
            var targetRoot = layer == null ? null : layer.targetModelOrPrefab;
            var texture = layer == null ? null : ResolveTargetTexture();
            var refresh = refreshRequested || cachedSourceModelOrPrefab != sourceRoot
                || cachedTargetModelOrPrefab != targetRoot || cachedTargetTexture != texture;
            if (refresh) candidateCache.Invalidate();
            cachedSourceModelOrPrefab = sourceRoot;
            cachedTargetModelOrPrefab = targetRoot;
            cachedTargetTexture = texture;
            refreshRequested = false;
            var changed = SynchronizePane(source, layer == null ? null : layer.sourceRegion, refresh);
            changed |= SynchronizePane(target, layer == null ? null : layer.targetRegion, refresh);
            if (selectedPoint >= (layer?.landmarks?.Count ?? 0)) selectedPoint = -1;
            return changed || refresh;
        }

        private static bool SynchronizePane(Pane pane, FBXUVTransferRegion region, bool refresh)
        {
            var changed = !pane.initialized || !ReferenceEquals(pane.analyzedRegion, region)
                || pane.savedMesh != region?.mesh || pane.savedSubmesh != (region?.subMeshIndex ?? 0)
                || pane.savedUv != (region?.uvChannel ?? 0) || pane.savedIslandId != (region?.islandId ?? -1)
                || pane.savedMeshHash != region?.meshHash;
            if (!changed && !refresh) return false;

            pane.savedMesh = region?.mesh;
            pane.savedSubmesh = region?.subMeshIndex ?? 0;
            pane.savedUv = region?.uvChannel ?? 0;
            pane.savedIslandId = region?.islandId ?? -1;
            pane.savedMeshHash = region?.meshHash;
            if (!pane.hasPendingRegionSelection)
            {
                pane.mesh = pane.savedMesh;
                pane.submesh = pane.savedSubmesh;
                pane.uv = pane.savedUv;
            }
            ReloadIslands(pane);
            AnalyzeBoundaries(pane, region);
            pane.initialized = true;
            return true;
        }

        private void OnGUI()
        {
            EditorGUI.BeginChangeCheck();
            var next = (FBXUVMakeupTransferLayer)EditorGUILayout.ObjectField("編集中", layer, typeof(FBXUVMakeupTransferLayer), true);
            if (EditorGUI.EndChangeCheck()) SetLayer(next);
            if (layer == null) { EditorGUILayout.HelpBox("メイク転送LayerのInspectorから開いてください。", MessageType.Info); return; }
            SynchronizeConfiguration();
            if (GUILayout.Button("安定化係数・補正設定・変形検査をInspectorで開く"))
            {
                Selection.activeGameObject = layer.gameObject;
                EditorGUIUtility.PingObject(layer);
                EditorApplication.ExecuteMenuItem("Window/General/Inspector");
            }
            selectIsland = GUILayout.Toolbar(selectIsland ? 0 : 1, new[] { "1. 顔のUV島を選択", "2. 対応点を編集" }) == 0;
            EditorGUILayout.HelpBox(selectIsland
                ? "左右のメッシュ・サブメッシュ・UVを選び、顔の肌のUV島をクリックしてください。"
                : "境界番号から口・左右の眼を指定できます。対応点の編集では一覧から点を選び、左右の画像でクリック・ドラッグします。", MessageType.None);
            using (new EditorGUILayout.HorizontalScope())
            {
                DrawPane(source, true);
                DrawPane(target, false);
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("目・口の境界候補を提案"))
                {
                    var sourceFound = SuggestBoundaries(source);
                    var targetFound = SuggestBoundaries(target);
                    selectIsland = false;
                    message = sourceFound && targetFound
                        ? "口・左右の眼の境界候補を選びました。画像で部位を確認してから、選んだ境界で初期配置してください。保存済みの対応点は変更していません。"
                        : "一意に提案できない側は境界を個別に選択してください。必要な閉境界がない場合は手動の目印を使ってください。";
                }
                if (GUILayout.Button("選んだ境界で初期配置（既存点を置換）"))
                {
                    if (FBXUVMakeupLandmarkUtility.TryGenerateFromLoops(source.boundaries, source.boundarySelection,
                        target.boundaries, target.boundarySelection, out var generated, out var error))
                    {
                        Change("メイク対応点を初期配置", () => layer.landmarks = generated);
                        selectedPoint = 0;
                        selectIsland = false;
                        message = "28点を初期配置しました。目・口の対応とメイクの位置を確認し、必要に応じて修正してください。";
                    }
                    else message = error;
                }
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("手動の目印28点を仮配置（既存点を置換）"))
                {
                    Change("メイク手動対応点の目印を配置", () => layer.landmarks = FBXUVMakeupLandmarkUtility.CreateManualTemplate(layer.sourceRegion, layer.targetRegion));
                    selectedPoint = 0; selectIsland = false;
                    message = "部位名付きの仮の位置です。口角・上下唇、左右の目頭・目尻・上下まぶたを、両方の画像で全て合わせてください。自動検出した位置ではありません。";
                }
                if (GUILayout.Button(new GUIContent("鼻先の候補を1点追加", FBXUVMakeupNoseUtility.SuggestionDescription), GUILayout.Width(170)))
                    TryAddNoseLandmark();
                if (GUILayout.Button("対応点を追加", GUILayout.Width(120)))
                {
                    Change("メイク対応点を追加", () => {
                        if (layer.landmarks == null) layer.landmarks = new List<FBXUVMakeupLandmark>();
                        layer.landmarks.Add(new FBXUVMakeupLandmark { name = "対応点 " + (layer.landmarks.Count + 1),
                            sourceUv = Vector2.one * .5f, targetUv = Vector2.one * .5f });
                    });
                    selectedPoint = layer.landmarks.Count - 1;
                    selectIsland = false;
                }
                using (new EditorGUI.DisabledScope(!HasSelectedPoint()))
                {
                    if (GUILayout.Button("選択点を削除", GUILayout.Width(120)))
                    {
                        Change("メイク対応点を削除", () => layer.landmarks.RemoveAt(selectedPoint));
                        selectedPoint = Mathf.Min(selectedPoint, layer.landmarks.Count - 1);
                    }
                }
            }
            if (!string.IsNullOrEmpty(message)) EditorGUILayout.HelpBox(message, MessageType.Info);
            DrawPoints();
            if (!layer.CanRender(out var renderError)) EditorGUILayout.HelpBox(renderError, MessageType.Warning);
        }

        private void DrawPane(Pane pane, bool isSource)
        {
            using (new EditorGUILayout.VerticalScope(GUILayout.Width((position.width - 24) * .5f)))
            {
                EditorGUILayout.LabelField(isSource ? "転送元UV：元の顔" : "転送先UV：転送先の顔", EditorStyles.boldLabel);
                var region = isSource ? layer.sourceRegion : layer.targetRegion;
                var options = CollectOptions(isSource, out var optionError);
                if (!isSource) TrySelectOnlyTargetCandidate(pane, options);
                var selected = DrawMeshSelection(pane, isSource, options);
                using (new EditorGUI.DisabledScope(pane.mesh == null || (!isSource &&
                    !FBXUVCanvasHierarchyUtility.TryFindCanvas(layer, out _, out _, out _))))
                {
                    EditorGUI.BeginChangeCheck();
                    pane.uv = EditorGUILayout.IntSlider("UVチャンネル", pane.uv, 0, 7);
                    if (EditorGUI.EndChangeCheck()) MarkRegionSelectionPending(pane);
                }
                if (!string.IsNullOrEmpty(optionError)) EditorGUILayout.HelpBox(optionError, MessageType.Warning);
                if (!string.IsNullOrEmpty(pane.error)) EditorGUILayout.HelpBox(pane.error, MessageType.Warning);
                var size = Mathf.Min((position.width - 40) * .5f, Mathf.Max(180, position.height - 570));
                var rect = GUILayoutUtility.GetRect(size, size, GUILayout.ExpandWidth(false));
                if (Event.current.type == EventType.Repaint)
                {
                    if (isSource) sourceImageRect = rect; else targetImageRect = rect;
                }
                EditorGUI.DrawRect(rect, new Color(.16f, .16f, .16f));
                var background = isSource ? (layer.sourceReferenceTexture != null ? layer.sourceReferenceTexture : layer.makeupTexture) : ResolveTargetTexture();
                if (background != null) EditorGUI.DrawPreviewTexture(rect, background, null, ScaleMode.StretchToFill);
                if (isSource && layer.sourceReferenceTexture != null && layer.makeupTexture != null)
                    GUI.DrawTexture(rect, layer.makeupTexture, ScaleMode.StretchToFill, true);
                if (Event.current.type == EventType.Repaint)
                {
                    Handles.BeginGUI();
                    if (selected >= 0 && selectIsland)
                    {
                        foreach (var island in pane.islands)
                        {
                            Handles.color = region != null && region.mesh == pane.mesh && region.subMeshIndex == pane.submesh
                                && region.uvChannel == pane.uv && region.islandId == island.id ? new Color(1, .65f, .15f, .9f) : new Color(.1f, .85f, 1, .25f);
                            var lines = new Vector3[island.triangles.Count * 6];
                            for (var i = 0; i < island.triangles.Count; i++)
                            {
                                var tri = island.triangles[i];
                                var a = ToGui(rect, tri.a); var b = ToGui(rect, tri.b); var c = ToGui(rect, tri.c);
                                lines[i * 6] = a; lines[i * 6 + 1] = b; lines[i * 6 + 2] = b;
                                lines[i * 6 + 3] = c; lines[i * 6 + 4] = c; lines[i * 6 + 5] = a;
                            }
                            Handles.DrawLines(lines);
                        }
                    }
                    if (!selectIsland && pane.boundaries != null && region != null && region.mesh == pane.mesh
                        && region.subMeshIndex == pane.submesh && region.uvChannel == pane.uv)
                    {
                        for (var index = 0; index < pane.boundaries.ClosedLoops.Count; index++)
                        {
                            var loop = pane.boundaries.ClosedLoops[index];
                            var color = index == pane.boundarySelection.mouth ? Color.green
                                : index == pane.boundarySelection.eyeLeftUv ? Color.cyan
                                : index == pane.boundarySelection.eyeRightUv ? Color.yellow : new Color(1, .5f, .15f, .65f);
                            Handles.color = color;
                            var lines = loop.Select(p => ToGui(rect, p)).Concat(new[] { ToGui(rect, loop[0]) }).ToArray();
                            Handles.DrawAAPolyLine(2, lines);
                            var center = ToGui(rect, FBXUVMakeupLandmarkUtility.LoopBounds(loop).Center);
                            GUI.Label(new Rect(center.x + 3, center.y + 3, 70, 20), "境界 " + (index + 1), EditorStyles.whiteMiniLabel);
                        }
                    }
                    if (layer.landmarks != null)
                    {
                        for (var i = 0; i < layer.landmarks.Count; i++)
                        {
                            var point = layer.landmarks[i];
                            if (point == null) continue;
                            var xy = ToGui(rect, isSource ? point.sourceUv : point.targetUv);
                            Handles.color = i == selectedPoint ? Color.yellow : Color.magenta;
                            Handles.DrawSolidDisc(xy, Vector3.forward, i == selectedPoint ? 5 : 3);
                            GUI.Label(new Rect(xy.x + 4, xy.y - 16, 45, 20), (i + 1).ToString(), EditorStyles.whiteMiniLabel);
                        }
                    }
                    Handles.EndGUI();
                }
                HandleInput(rect, pane, isSource, selected >= 0);
                if (!selectIsland) DrawBoundarySelectors(pane);
            }
        }

        private int DrawMeshSelection(Pane pane, bool isSource, List<FBXUVMeshOption> options)
        {
            var snapshot = isSource ? candidateCache.Source : candidateCache.Target;
            var selected = options.FindIndex(o => o.Mesh == pane.mesh && (isSource || o.SubMeshIndex == pane.submesh));
            var label = isSource ? "メッシュ" : "メッシュ / サブメッシュ";
            if (selected >= 0)
            {
                EditorGUI.BeginChangeCheck();
                var next = EditorGUILayout.Popup(label, selected, snapshot.Labels);
                if (EditorGUI.EndChangeCheck()) ApplyMeshSelection(pane, options[next]);
            }
            else
            {
                using (new EditorGUI.DisabledScope(true))
                {
                    EditorGUILayout.ObjectField("現在のRegionのメッシュ", pane.mesh, typeof(Mesh), false);
                    EditorGUILayout.IntField("現在のRegionのサブメッシュ", pane.submesh);
                }
                EditorGUILayout.HelpBox("現在の領域のメッシュが候補にありません。保存済みのUV島は保持しています。候補を選び、UV島をクリックして確定してください。", MessageType.Warning);
                if (options.Count > 0)
                {
                    var labels = snapshot.ReplacementLabels;
                    var next = EditorGUILayout.Popup(isSource ? "転送元メッシュ" : "転送先メッシュ / サブメッシュ", 0, labels) - 1;
                    if (next >= 0) ApplyMeshSelection(pane, options[next]);
                }
            }
            if (isSource)
            {
                using (new EditorGUI.DisabledScope(pane.mesh == null))
                {
                    EditorGUI.BeginChangeCheck();
                    pane.submesh = EditorGUILayout.IntSlider("サブメッシュ", pane.submesh, 0,
                        pane.mesh == null ? 0 : Mathf.Max(0, pane.mesh.subMeshCount - 1));
                    if (EditorGUI.EndChangeCheck()) MarkRegionSelectionPending(pane);
                }
            }
            return options.FindIndex(o => o.Mesh == pane.mesh && (isSource || o.SubMeshIndex == pane.submesh));
        }

        private static void ApplyMeshSelection(Pane pane, FBXUVMeshOption option)
        {
            pane.mesh = option.Mesh;
            pane.submesh = option.SubMeshIndex;
            MarkRegionSelectionPending(pane);
        }

        private static void MarkRegionSelectionPending(Pane pane)
        {
            pane.hasPendingRegionSelection = true;
            ReloadIslands(pane);
        }

        private static bool TrySelectOnlyTargetCandidate(Pane pane, List<FBXUVMeshOption> options)
        {
            if (options.Count != 1 || (pane.mesh == options[0].Mesh && pane.submesh == options[0].SubMeshIndex)) return false;
            ApplyMeshSelection(pane, options[0]);
            return true;
        }

        private static void AnalyzeBoundaries(Pane pane, FBXUVTransferRegion region)
        {
            pane.analyzedRegion = region;
            pane.boundarySelection = FBXUVMakeupLandmarkUtility.BoundarySelection.Empty;
            FBXUVMakeupLandmarkUtility.TryAnalyze(region, out pane.boundaries, out pane.boundaryError);
            // This only proposes popup choices; it never changes the component's saved landmarks.
            SuggestBoundaries(pane);
        }

        private static bool SuggestBoundaries(Pane pane)
        {
            if (FBXUVMakeupLandmarkUtility.TrySuggestLoops(pane.boundaries, out var selection, out var error))
            {
                pane.boundarySelection = selection;
                pane.boundaryError = null;
                return true;
            }
            pane.boundaryError = error;
            return false;
        }

        private static void DrawBoundarySelectors(Pane pane)
        {
            if (pane.boundaries == null)
            {
                EditorGUILayout.HelpBox(pane.boundaryError ?? "顔のUV島を選択してください。", MessageType.Info);
                return;
            }
            var labels = new[] { "<境界を選択>" }.Concat(pane.boundaries.ClosedLoops.Select((loop, i) =>
                "境界 " + (i + 1) + " (" + loop.Count + "頂点)")).ToArray();
            pane.boundarySelection.mouth = EditorGUILayout.Popup("口（緑）", pane.boundarySelection.mouth + 1, labels) - 1;
            pane.boundarySelection.eyeLeftUv = EditorGUILayout.Popup("UV左眼（水色）", pane.boundarySelection.eyeLeftUv + 1, labels) - 1;
            pane.boundarySelection.eyeRightUv = EditorGUILayout.Popup("UV右眼（黄）", pane.boundarySelection.eyeRightUv + 1, labels) - 1;
            EditorGUILayout.LabelField(pane.boundaries.Diagnostics, EditorStyles.miniLabel);
            if (!string.IsNullOrEmpty(pane.boundaryError)) EditorGUILayout.HelpBox(pane.boundaryError, MessageType.Info);
        }

        private void HandleInput(Rect rect, Pane pane, bool isSource, bool validMesh)
        {
            var current = Event.current;
            var control = GUIUtility.GetControlID(isSource ? 891341 : 891342, FocusType.Passive, rect);
            if (current.type == EventType.MouseDown && current.button == 0 && rect.Contains(current.mousePosition))
            {
                var uv = ToUv(rect, current.mousePosition);
                if (selectIsland)
                {
                    if (validMesh)
                    {
                        var island = FBXUVIslandExtractor.HitTest(pane.islands, uv);
                        if (island != null)
                        {
                            Change("メイクの顔UV島を選択", () => {
                                var region = new FBXUVTransferRegion { mesh = pane.mesh, subMeshIndex = pane.submesh, uvChannel = pane.uv,
                                    islandId = island.id, triangles = new List<FBXUVTriangle>(island.triangles), bounds = island.bounds,
                                    meshHash = FBXUVMeshUtility.ComputeMeshContentHash(pane.mesh, pane.submesh, pane.uv) };
                                if (isSource) layer.sourceRegion = region; else layer.targetRegion = region;
                            });
                            pane.hasPendingRegionSelection = false;
                            if (layer.landmarks != null && layer.landmarks.Count > 0)
                                message = "顔のUV島を変更しました。既存の対応点の位置を再確認するか、目・口から初期配置し直してください。";
                        }
                        else ShowNotification(new GUIContent("この位置にUV島はありません。"));
                    }
                    else ShowNotification(new GUIContent("現在のModel / Prefab候補からメッシュを選択してください。"));
                }
                else
                {
                    var hit = FindPoint(rect, isSource, current.mousePosition);
                    if (hit >= 0) selectedPoint = hit;
                    if (HasSelectedPoint())
                    {
                        Undo.IncrementCurrentGroup();
                        dragUndoGroup = Undo.GetCurrentGroup();
                        GUIUtility.hotControl = dragControl = control;
                        MovePoint(isSource, uv);
                    }
                }
                current.Use(); Repaint();
            }
            else if (current.type == EventType.MouseDrag && GUIUtility.hotControl == control && dragControl == control)
            {
                MovePoint(isSource, ToUv(rect, current.mousePosition));
                current.Use(); Repaint();
            }
            else if (current.type == EventType.MouseUp && dragControl == control)
            {
                EndDrag(); current.Use();
            }
        }

        private void DrawPoints()
        {
            if (layer.landmarks == null || layer.landmarks.Count == 0) return;
            using (var scroll = new EditorGUILayout.ScrollViewScope(pointsScroll, GUILayout.MaxHeight(160)))
            {
                pointsScroll = scroll.scrollPosition;
                for (var i = 0; i < layer.landmarks.Count; i++)
                {
                    var point = layer.landmarks[i];
                    if (point == null) continue;
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        if (GUILayout.Toggle(selectedPoint == i, (i + 1).ToString(), "Button", GUILayout.Width(36))) selectedPoint = i;
                        EditorGUI.BeginChangeCheck();
                        var name = EditorGUILayout.DelayedTextField(point.name, GUILayout.MinWidth(100));
                        var sourceUv = EditorGUILayout.Vector2Field(GUIContent.none, point.sourceUv, GUILayout.MinWidth(170));
                        var targetUv = EditorGUILayout.Vector2Field(GUIContent.none, point.targetUv, GUILayout.MinWidth(170));
                        if (EditorGUI.EndChangeCheck()) Change("メイク対応点を編集", () => { point.name = name; point.sourceUv = ClampUv(sourceUv); point.targetUv = ClampUv(targetUv); });
                    }
                }
            }
        }

        private List<FBXUVMeshOption> CollectOptions(bool isSource, out string error)
        {
            var snapshot = isSource
                ? candidateCache.GetSource(layer.sourceModelOrPrefab, out error)
                : candidateCache.GetTarget(layer, layer.targetModelOrPrefab, ResolveTargetTexture(), out error);
            if (string.IsNullOrEmpty(error) && snapshot.Options.Count == 0)
                error = isSource ? "モデルにメッシュがありません。"
                    : "Canvasの対象テクスチャを使用するメッシュがありません。転送先 Model / PrefabとCanvasを確認してください。";
            return snapshot.Options;
        }

        private Texture ResolveTargetTexture() => FBXUVCanvasHierarchyUtility.TryFindCanvas(layer, out var canvas, out _, out _) ? canvas.TargetTexture?.SelectTexture : null;
        private static void ReloadIslands(Pane pane)
        {
            pane.islands.Clear(); pane.error = null;
            if (pane.mesh == null) return;
            try { pane.islands = FBXUVIslandExtractor.Extract(pane.mesh, pane.submesh, pane.uv); }
            catch (Exception exception) { pane.error = exception.Message; }
            if (pane.islands.Count == 0 && pane.error == null) pane.error = "このUVチャンネルに選択できるUV島がありません。";
        }

        private void Change(string label, Action action)
        {
            Undo.RecordObject(layer, label);
            action();
            EditorUtility.SetDirty(layer);
            PrefabUtility.RecordPrefabInstancePropertyModifications(layer);
            Repaint();
        }
        private void MovePoint(bool isSource, Vector2 uv)
        {
            if (!HasSelectedPoint()) return;
            Change("メイク対応点を移動", () => { if (isSource) layer.landmarks[selectedPoint].sourceUv = uv; else layer.landmarks[selectedPoint].targetUv = uv; });
        }
        private bool HasSelectedPoint() => layer?.landmarks != null && selectedPoint >= 0 && selectedPoint < layer.landmarks.Count && layer.landmarks[selectedPoint] != null;
        private void EndDrag()
        {
            if (dragControl != 0 && GUIUtility.hotControl == dragControl) GUIUtility.hotControl = 0;
            if (dragUndoGroup >= 0) Undo.CollapseUndoOperations(dragUndoGroup);
            dragControl = 0; dragUndoGroup = -1;
        }
        private int FindPoint(Rect rect, bool isSource, Vector2 mouse)
        {
            if (layer.landmarks == null) return -1;
            var nearest = -1;
            var distance = 8f;
            for (var i = 0; i < layer.landmarks.Count; i++)
            {
                var p = layer.landmarks[i];
                if (p == null) continue;
                var candidateDistance = Vector2.Distance(ToGui(rect, isSource ? p.sourceUv : p.targetUv), mouse);
                if (candidateDistance > distance) continue;
                nearest = i; distance = candidateDistance;
            }
            return nearest;
        }
        private static Vector3 ToGui(Rect rect, Vector2 uv) => new Vector3(rect.x + uv.x * rect.width, rect.yMax - uv.y * rect.height, 0);
        private static Vector2 ToUv(Rect rect, Vector2 point) => ClampUv(new Vector2((point.x - rect.x) / rect.width, (rect.yMax - point.y) / rect.height));
        private static Vector2 ClampUv(Vector2 uv) => new Vector2(Mathf.Clamp01(uv.x), Mathf.Clamp01(uv.y));
    }
}
