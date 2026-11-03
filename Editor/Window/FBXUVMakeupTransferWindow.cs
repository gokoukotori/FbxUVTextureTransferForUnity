using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace GokouKotori.FBXUVTextureTransfer.Editor
{
    // Makeup transfer: face island selection and landmark editing on two shared region picker panes.
    public sealed class FBXUVMakeupTransferWindow : FBXUVLayerWindow<FBXUVMakeupTransferLayer>
    {
        private const string WindowTitle = "メイク転送";

        [SerializeField] internal bool selectIsland = true;
        [SerializeField] internal int selectedPoint = -1;
        // Copied from the panes' PreviewScreenRect (relative to the window origin).
        [NonSerialized] internal Rect sourceImageRect;
        [NonSerialized] internal Rect targetImageRect;
        [NonSerialized] internal string message;
        [NonSerialized] private Vector2 pointsScroll;
        [NonSerialized] private FBXUVRegionPickerPane sourcePane;
        [NonSerialized] private FBXUVRegionPickerPane targetPane;
        [NonSerialized] private int dragControl;
        [NonSerialized] private int dragUndoGroup = -1;
        private readonly MakeupBoundaryState sourceBoundary = new MakeupBoundaryState();
        private readonly MakeupBoundaryState targetBoundary = new MakeupBoundaryState();

        // Created lazily: the base analysis cache is not initialized yet while derived fields are.
        internal FBXUVRegionPickerPane SourcePane => sourcePane ??= new FBXUVRegionPickerPane("転送元：元の顔", AnalysisCache);
        internal FBXUVRegionPickerPane TargetPane => targetPane ??= new FBXUVRegionPickerPane("転送先：転送先の顔", AnalysisCache);

        protected override GameObject SourceModelOrPrefab => Layer == null ? null : Layer.sourceModelOrPrefab;
        protected override GameObject TargetModelOrPrefab => Layer == null ? null : Layer.targetModelOrPrefab;
        protected override string MissingLayerMessage => "メイク転送LayerのInspectorから開いてください。";

        // Boundary loops of one saved face island. Suggestions only fill the popups; they never change saved landmarks.
        private sealed class MakeupBoundaryState
        {
            internal FBXUVTransferRegion analyzedRegion;
            internal FBXUVMakeupLandmarkUtility.BoundaryAnalysis boundaries;
            internal FBXUVMakeupLandmarkUtility.BoundarySelection boundarySelection = FBXUVMakeupLandmarkUtility.BoundarySelection.Empty;
            internal string boundaryError;

            internal void Analyze(FBXUVTransferRegion region)
            {
                analyzedRegion = region;
                boundaries = null;
                boundaryError = null;
                boundarySelection = FBXUVMakeupLandmarkUtility.BoundarySelection.Empty;
                if (region == null) return;
                FBXUVMakeupLandmarkUtility.TryAnalyze(region, out boundaries, out boundaryError);
                Suggest();
            }

            internal bool Suggest()
            {
                if (FBXUVMakeupLandmarkUtility.TrySuggestLoops(boundaries, out var selection, out var error))
                {
                    boundarySelection = selection;
                    boundaryError = null;
                    return true;
                }
                boundaryError = error;
                return false;
            }

            internal void DrawLoops(Rect image)
            {
                for (var index = 0; index < boundaries.ClosedLoops.Count; index++)
                {
                    var loop = boundaries.ClosedLoops[index];
                    Handles.color = index == boundarySelection.mouth ? Color.green
                        : index == boundarySelection.eyeLeftUv ? Color.cyan
                        : index == boundarySelection.eyeRightUv ? Color.yellow : new Color(1, .5f, .15f, .65f);
                    var lines = loop.Select(p => FBXUVPreviewViewport.ToGui(image, p)).Concat(new[] { FBXUVPreviewViewport.ToGui(image, loop[0]) }).ToArray();
                    Handles.DrawAAPolyLine(2, lines);
                    var center = FBXUVPreviewViewport.ToGui(image, FBXUVMakeupLandmarkUtility.LoopBounds(loop).Center);
                    GUI.Label(new Rect(center.x + 3, center.y + 3, 70, 20), "境界 " + (index + 1), EditorStyles.whiteMiniLabel);
                }
            }

            internal void DrawSelectors()
            {
                if (boundaries == null)
                {
                    EditorGUILayout.HelpBox(boundaryError ?? "顔のUV島を選択してください。", MessageType.Info);
                    return;
                }
                var labels = new[] { "<境界を選択>" }.Concat(boundaries.ClosedLoops.Select((loop, i) =>
                    "境界 " + (i + 1) + " (" + loop.Count + "頂点)")).ToArray();
                boundarySelection.mouth = EditorGUILayout.Popup("口（緑）", boundarySelection.mouth + 1, labels) - 1;
                boundarySelection.eyeLeftUv = EditorGUILayout.Popup("UV左眼（水色）", boundarySelection.eyeLeftUv + 1, labels) - 1;
                boundarySelection.eyeRightUv = EditorGUILayout.Popup("UV右眼（黄）", boundarySelection.eyeRightUv + 1, labels) - 1;
                EditorGUILayout.LabelField(boundaries.Diagnostics, EditorStyles.miniLabel);
                if (!string.IsNullOrEmpty(boundaryError)) EditorGUILayout.HelpBox(boundaryError, MessageType.Info);
            }
        }

        [MenuItem("Tools/Gokoukotori/FBX UV Texture Transfer/Makeup Transfer Editor")]
        public static void Open()
        {
            GetWindow<FBXUVMakeupTransferWindow>(WindowTitle);
        }

        public static void Open(FBXUVMakeupTransferLayer layer)
        {
            var window = GetWindow<FBXUVMakeupTransferWindow>(WindowTitle);
            window.SetLayer(layer);
            window.Show();
            window.Focus();
        }

        internal static void RepaintFor(FBXUVMakeupTransferLayer layer)
        {
            if (layer == null) return;
            foreach (var window in Resources.FindObjectsOfTypeAll<FBXUVMakeupTransferWindow>())
                if (window.Layer == layer) window.Repaint();
        }

        internal bool TryAddNoseLandmark()
        {
            if (!FBXUVMakeupNoseUtility.TryAdd(Layer, out var index, out var error))
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

        internal List<FBXUVMeshOption> CollectOptions(bool isSource, out string error) => Candidates(isSource, out error).Options;

        protected override void OnEnable()
        {
            base.OnEnable();
            minSize = new Vector2(900, 780);
            RegisterPanes(SourcePane, TargetPane);
        }

        protected override void OnDisable()
        {
            base.OnDisable();
            EndDrag();
        }

        protected override void OnLayerChanged()
        {
            EndDrag();
            selectedPoint = -1;
            message = null;
            pointsScroll = Vector2.zero;
        }

        internal override void OnRefresh(FBXUVRefreshKind kind)
        {
            EndDrag();
            SyncPane(true, kind);
            SyncPane(false, kind);
        }

        // In-place edits (Inspector, scripts) change the saved regions without an Undo event.
        // A struct comparison per pane; landmark edits do not count as saved region changes.
        protected override bool DetectSavedValueChanges()
        {
            return Layer != null
                && (!FBXUVSavedRegion.From(Layer.sourceRegion).Equals(SourcePane.Saved)
                    || !FBXUVSavedRegion.From(Layer.targetRegion).Equals(TargetPane.Saved));
        }

        protected override void DrawContent()
        {
            if (selectedPoint >= (Layer.landmarks?.Count ?? 0)) selectedPoint = -1;
            if (GUILayout.Button("安定化係数・補正設定・変形検査をInspectorで開く"))
            {
                Selection.activeGameObject = Layer.gameObject;
                EditorGUIUtility.PingObject(Layer);
                EditorApplication.ExecuteMenuItem("Window/General/Inspector");
            }
            var nextSelectIsland = GUILayout.Toolbar(selectIsland ? 0 : 1, new[] { "1. 顔のUV島を選択", "2. 対応点を編集" }) == 0;
            if (nextSelectIsland != selectIsland)
            {
                selectIsland = nextSelectIsland;
                // The panes' controls (island popup, boundary selectors) depend on the mode.
                GUIUtility.ExitGUI();
            }
            EditorGUILayout.HelpBox(selectIsland
                ? "左右のメッシュ・サブメッシュ・UVを選び、顔の肌のUV島をクリックしてください。"
                : "境界番号から口・左右の眼を指定できます。対応点の編集では一覧から点を選び、左右の画像でクリック・ドラッグします。", MessageType.None);
            var size = PreviewSize(position.width, 2);
            using (new EditorGUILayout.HorizontalScope())
            {
                DrawPane(true, size);
                GUILayout.Space(16f);
                DrawPane(false, size);
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("目・口の境界候補を提案"))
                {
                    var sourceFound = sourceBoundary.Suggest();
                    var targetFound = targetBoundary.Suggest();
                    selectIsland = false;
                    message = sourceFound && targetFound
                        ? "口・左右の眼の境界候補を選びました。画像で部位を確認してから、選んだ境界で初期配置してください。保存済みの対応点は変更していません。"
                        : "一意に提案できない側は境界を個別に選択してください。必要な閉境界がない場合は手動の目印を使ってください。";
                }
                if (GUILayout.Button("選んだ境界で初期配置（既存点を置換）"))
                {
                    if (FBXUVMakeupLandmarkUtility.TryGenerateFromLoops(sourceBoundary.boundaries, sourceBoundary.boundarySelection,
                        targetBoundary.boundaries, targetBoundary.boundarySelection, out var generated, out var error))
                    {
                        RecordChange("メイク対応点を初期配置", () => Layer.landmarks = generated);
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
                    RecordChange("メイク手動対応点の目印を配置", () => Layer.landmarks = FBXUVMakeupLandmarkUtility.CreateManualTemplate(Layer.sourceRegion, Layer.targetRegion));
                    selectedPoint = 0; selectIsland = false;
                    message = "部位名付きの仮の位置です。口角・上下唇、左右の目頭・目尻・上下まぶたを、両方の画像で全て合わせてください。自動検出した位置ではありません。";
                }
                if (GUILayout.Button(new GUIContent("鼻先の候補を1点追加", FBXUVMakeupNoseUtility.SuggestionDescription), GUILayout.Width(170)))
                    TryAddNoseLandmark();
                if (GUILayout.Button("対応点を追加", GUILayout.Width(120)))
                {
                    RecordChange("メイク対応点を追加", () => {
                        if (Layer.landmarks == null) Layer.landmarks = new List<FBXUVMakeupLandmark>();
                        Layer.landmarks.Add(new FBXUVMakeupLandmark { name = "対応点 " + (Layer.landmarks.Count + 1),
                            sourceUv = Vector2.one * .5f, targetUv = Vector2.one * .5f });
                    });
                    selectedPoint = Layer.landmarks.Count - 1;
                    selectIsland = false;
                }
                using (new EditorGUI.DisabledScope(!HasSelectedPoint()))
                {
                    if (GUILayout.Button("選択点を削除", GUILayout.Width(120)))
                    {
                        RecordChange("メイク対応点を削除", () => Layer.landmarks.RemoveAt(selectedPoint));
                        selectedPoint = Mathf.Min(selectedPoint, Layer.landmarks.Count - 1);
                    }
                }
            }
            if (!string.IsNullOrEmpty(message)) EditorGUILayout.HelpBox(message, MessageType.Info);
            DrawPoints();
            if (!Layer.CanRender(out var renderError)) EditorGUILayout.HelpBox(renderError, MessageType.Warning);
        }

        private void DrawPane(bool isSource, float size)
        {
            var pane = isSource ? SourcePane : TargetPane;
            var boundary = isSource ? sourceBoundary : targetBoundary;
            var candidates = Candidates(isSource, out var error);
            SyncPane(isSource, FBXUVRefreshKind.None);
            pane.TrySelectOnlyCandidate(candidates);
            var context = CreatePaneContext(
                candidates,
                error,
                !isSource,
                () => isSource
                    ? FBXUVCandidateMessages.Source(SourceModelOrPrefab, candidates.Options.Count, pane.Mesh != null)
                    : FBXUVCandidateMessages.Target(Layer, ResolveTargetTexture(), candidates.Options.Count, pane.Mesh != null),
                !isSource ? ResolveTargetTexture()
                    : Layer.sourceReferenceTexture != null ? Layer.sourceReferenceTexture : Layer.makeupTexture,
                size);
            context.OverlayTexture = isSource && Layer.sourceReferenceTexture != null ? Layer.makeupTexture : null;
            context.IslandSelectionEnabled = selectIsland;
            context.DrawOverlay = image => DrawOverlay(pane, boundary, isSource, image);
            context.HandleInput = (local, image) => HandleInput(local, image, isSource);
            FBXUVRegionPick? pick;
            using (new EditorGUILayout.VerticalScope(GUILayout.Width(size + 16f)))
            {
                pick = pane.Draw(context);
                if (!selectIsland) boundary.DrawSelectors();
            }
            if (isSource) sourceImageRect = pane.PreviewScreenRect; else targetImageRect = pane.PreviewScreenRect;
            if (!pick.HasValue) return;
            StoreIsland(isSource, pick.Value);
        }

        // Boundary analysis follows the saved region (value change, new region object, Undo / layer change).
        private void SyncPane(bool isSource, FBXUVRefreshKind kind)
        {
            var region = Layer == null ? null : isSource ? Layer.sourceRegion : Layer.targetRegion;
            var boundary = isSource ? sourceBoundary : targetBoundary;
            var changed = (isSource ? SourcePane : TargetPane).Sync(FBXUVSavedRegion.From(region), kind);
            if (changed || !ReferenceEquals(boundary.analyzedRegion, region)) boundary.Analyze(region);
        }

        private void StoreIsland(bool isSource, in FBXUVRegionPick pick)
        {
            var region = FBXUVRegionFactory.Create(pick, AnalysisCache);
            RecordChange("メイクの顔UV島を選択", () => {
                if (isSource) Layer.sourceRegion = region; else Layer.targetRegion = region;
            });
            (isSource ? SourcePane : TargetPane).MarkSaved(FBXUVSavedRegion.From(region));
            if (Layer.landmarks != null && Layer.landmarks.Count > 0)
                message = "顔のUV島を変更しました。既存の対応点の位置を再確認するか、目・口から初期配置し直してください。";
        }

        private void DrawOverlay(FBXUVRegionPickerPane pane, MakeupBoundaryState boundary, bool isSource, Rect image)
        {
            var saved = pane.Saved;
            if (!selectIsland && boundary.boundaries != null && saved.HasMesh && pane.Mesh == saved.Mesh
                && pane.SubMeshIndex == saved.SubMeshIndex && pane.UvChannel == saved.UvChannel)
                boundary.DrawLoops(image);
            if (Layer.landmarks == null) return;
            for (var i = 0; i < Layer.landmarks.Count; i++)
            {
                var point = Layer.landmarks[i];
                if (point == null) continue;
                var xy = FBXUVPreviewViewport.ToGui(image, isSource ? point.sourceUv : point.targetUv);
                Handles.color = i == selectedPoint ? Color.yellow : Color.magenta;
                Handles.DrawSolidDisc(xy, Vector3.forward, i == selectedPoint ? 5 : 3);
                GUI.Label(new Rect(xy.x + 4, xy.y - 16, 45, 20), (i + 1).ToString(), EditorStyles.whiteMiniLabel);
            }
        }

        // Called by the pane for every event type, before its island hit test.
        private bool HandleInput(Rect local, Rect image, bool isSource)
        {
            var current = Event.current;
            var control = GUIUtility.GetControlID(isSource ? 891341 : 891342, FocusType.Passive, local);
            var leftDown = current.type == EventType.MouseDown && current.button == 0 && local.Contains(current.mousePosition);
            // Island mode: the pane handles the click (including the no-mesh notification).
            if (selectIsland) return false;
            if (leftDown)
            {
                var hit = FindPoint(image, isSource, current.mousePosition);
                if (hit >= 0) selectedPoint = hit;
                if (HasSelectedPoint())
                {
                    // One Undo group per drag; EndDrag collapses it.
                    Undo.IncrementCurrentGroup();
                    dragUndoGroup = Undo.GetCurrentGroup();
                    GUIUtility.hotControl = dragControl = control;
                    MovePoint(isSource, ClampUv(FBXUVPreviewViewport.ToUv(image, current.mousePosition)));
                }
            }
            else if (current.type == EventType.MouseDrag && GUIUtility.hotControl == control && dragControl == control)
                MovePoint(isSource, ClampUv(FBXUVPreviewViewport.ToUv(image, current.mousePosition)));
            else if (current.type == EventType.MouseUp && dragControl == control)
                EndDrag();
            else return false;
            current.Use();
            Repaint();
            return true;
        }

        private void DrawPoints()
        {
            if (Layer.landmarks == null || Layer.landmarks.Count == 0) return;
            // The base scroll view wraps the whole window; the list keeps its own bounded scroll.
            using (var scroll = new EditorGUILayout.ScrollViewScope(pointsScroll, GUILayout.MaxHeight(160)))
            {
                pointsScroll = scroll.scrollPosition;
                for (var i = 0; i < Layer.landmarks.Count; i++)
                {
                    var point = Layer.landmarks[i];
                    if (point == null) continue;
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        if (GUILayout.Toggle(selectedPoint == i, (i + 1).ToString(), "Button", GUILayout.Width(36))) selectedPoint = i;
                        EditorGUI.BeginChangeCheck();
                        var name = EditorGUILayout.DelayedTextField(point.name, GUILayout.MinWidth(100));
                        var sourceUv = EditorGUILayout.Vector2Field(GUIContent.none, point.sourceUv, GUILayout.MinWidth(170));
                        var targetUv = EditorGUILayout.Vector2Field(GUIContent.none, point.targetUv, GUILayout.MinWidth(170));
                        if (EditorGUI.EndChangeCheck()) RecordChange("メイク対応点を編集", () => { point.name = name; point.sourceUv = ClampUv(sourceUv); point.targetUv = ClampUv(targetUv); });
                    }
                }
            }
        }

        private FBXUVMeshCandidateCache.Snapshot Candidates(bool isSource, out string error)
        {
            return isSource
                ? CandidateCache.GetSource(SourceModelOrPrefab, FBXUVCandidateGrouping.ByMesh, out error)
                : CandidateCache.GetTarget(Layer, TargetModelOrPrefab, ResolveTargetTexture(), FBXUVCandidateGrouping.ByMesh, out error);
        }

        private void MovePoint(bool isSource, Vector2 uv)
        {
            if (!HasSelectedPoint()) return;
            RecordChange("メイク対応点を移動", () => { if (isSource) Layer.landmarks[selectedPoint].sourceUv = uv; else Layer.landmarks[selectedPoint].targetUv = uv; });
        }
        private bool HasSelectedPoint() => Layer != null && Layer.landmarks != null && selectedPoint >= 0 && selectedPoint < Layer.landmarks.Count && Layer.landmarks[selectedPoint] != null;
        private void EndDrag()
        {
            if (dragControl != 0 && GUIUtility.hotControl == dragControl) GUIUtility.hotControl = 0;
            if (dragUndoGroup >= 0) Undo.CollapseUndoOperations(dragUndoGroup);
            dragControl = 0; dragUndoGroup = -1;
        }
        private int FindPoint(Rect rect, bool isSource, Vector2 mouse)
        {
            if (Layer.landmarks == null) return -1;
            var nearest = -1;
            var distance = 8f;
            for (var i = 0; i < Layer.landmarks.Count; i++)
            {
                var p = Layer.landmarks[i];
                if (p == null) continue;
                var candidateDistance = Vector2.Distance(FBXUVPreviewViewport.ToGui(rect, isSource ? p.sourceUv : p.targetUv), mouse);
                if (candidateDistance > distance) continue;
                nearest = i; distance = candidateDistance;
            }
            return nearest;
        }
        private static Vector2 ClampUv(Vector2 uv) => new Vector2(Mathf.Clamp01(uv.x), Mathf.Clamp01(uv.y));
    }
}
