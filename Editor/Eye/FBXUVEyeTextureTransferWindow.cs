using System;
using System.Collections.Generic;
using System.Linq;
using GokouKotori.FBXUVTextureTransfer.Editor.NDMF;
using UnityEditor;
using UnityEngine;

namespace GokouKotori.FBXUVTextureTransfer.Editor
{
    // Eye transfer: eye list, per-eye configuration, three region picker panes (iris / pupil / target) and manual correction.
    public sealed class FBXUVEyeTextureTransferWindow : FBXUVLayerWindow<FBXUVEyeTextureTransferLayer>
    {
        private const int IrisPane = 0;
        private const int PupilPane = 1;
        private const int TargetPane = 2;
        // The settings the panes are shown with while the layer has no eyes; AddEye creates an eye with the same defaults.
        private static readonly FBXUVEyeBinding NewEye = new FBXUVEyeBinding();

        [SerializeField] internal int selectedEye;
        [SerializeField] private int editingTab;
        // Copied from the panes' PreviewScreenRect (relative to the window origin); default while a pane is hidden.
        [NonSerialized] internal readonly Rect[] imageRects = new Rect[3];
        [NonSerialized] internal Rect transferMethodRect;
        [NonSerialized] internal Rect pupilModeRect;
        [NonSerialized] internal FBXUVMessage measurement;
        // Layer dirty count when the results were produced; edits of any setting (including the Inspector) bump it.
        [NonSerialized] private int resultsDirtyCount;
        [NonSerialized] private FBXUVRegionPickerPane[] panes;
        [NonSerialized] private int syncedEyeCount = -1;
        private readonly FBXUVEyeManualEditor manualEditor = new FBXUVEyeManualEditor();
        private readonly FBXUVEntryListControl eyeList = new FBXUVEntryListControl("目", "虹彩・瞳孔・転送先のUV島の選択と手補正も削除されます。");

        // Created lazily: the base analysis cache is not initialized yet while derived fields are.
        internal FBXUVRegionPickerPane[] Panes => panes ??= new[]
        {
            new FBXUVRegionPickerPane("転送元：虹彩", AnalysisCache),
            new FBXUVRegionPickerPane("転送元：瞳孔", AnalysisCache),
            new FBXUVRegionPickerPane("転送先：目", AnalysisCache)
        };

        protected override string WindowTitle => FBXUVEditorNames.EyeTransferEditor;

        internal static readonly string[] EditingTabLabels = { "1. UV島を選択", "2. 手補正" };

        [MenuItem(FBXUVEditorNames.MenuRoot + FBXUVEditorNames.EyeTransferEditor, priority = 102)]
        public static void Open()
        {
            GetWindow<FBXUVEyeTextureTransferWindow>(FBXUVEditorNames.EyeTransferEditor).AdoptSelectedLayerIfEmpty();
        }

        public static void Open(FBXUVEyeTextureTransferLayer layer)
        {
            var window = GetWindow<FBXUVEyeTextureTransferWindow>(FBXUVEditorNames.EyeTransferEditor);
            window.SetLayer(layer);
            window.Show();
            window.Focus();
        }

        protected override void OnEnable()
        {
            base.OnEnable();
            minSize = new Vector2(960, 680);
            RegisterPanes(Panes);
        }

        protected override void OnDisable()
        {
            base.OnDisable();
            manualEditor.Dispose();
        }

        private void OnLostFocus() => manualEditor.CancelDrag();

        protected override void OnLayerChanged()
        {
            manualEditor.Reset();
            eyeList.ClearNameError();
            measurement = default;
            syncedEyeCount = -1;
        }

        internal override void OnRefresh(FBXUVRefreshKind kind)
        {
            // Eye switches restore the saved values; other refreshes keep the hand-correction editor's previews and selection.
            if (kind == FBXUVRefreshKind.RestoreSaved) manualEditor.Reset();
            measurement = default;
            syncedEyeCount = EyeCount();
            selectedEye = Mathf.Clamp(selectedEye, 0, Mathf.Max(0, syncedEyeCount - 1));
            // The hidden pupil pane is synced too, so DetectSavedValueChanges stays quiet.
            for (var index = 0; index < Panes.Length; index++) Panes[index].Sync(SavedRegion(index), kind);
        }

        // In-place edits (Inspector, scripts) change the saved regions without an Undo event: a struct comparison per pane.
        // Eyes added or removed elsewhere may move the selected index to another eye, so those reload the saved values.
        protected override bool DetectSavedValueChanges()
        {
            if (Layer == null) return false;
            if (EyeCount() != syncedEyeCount)
            {
                RequestRefresh(FBXUVRefreshKind.RestoreSaved);
                return true;
            }
            for (var index = 0; index < Panes.Length; index++)
                if (!SavedRegion(index).Equals(Panes[index].Saved)) return true;
            return false;
        }

        protected override void DrawContent()
        {
            if (!measurement.IsEmpty && EditorUtility.GetDirtyCount(Layer) != resultsDirtyCount) ClearResults();
            DrawEyeSelection();
            if (EyeCount() == 0)
            {
                // The panes are shown before any eye exists: the first island picked adds one (StoreIsland).
                EditorGUILayout.HelpBox("目はまだありません。UV島を選ぶと、目を1つ自動で追加します。左右の目が別のUV島なら、もう片方は「目を追加」で追加してください。", MessageType.Info);
                DrawRegionSelection(NewEye, NewEye.pupilMode == FBXUVEyePupilMode.Separate);
                return;
            }
            var eye = CurrentEye();
            if (eye == null)
            {
                EditorGUILayout.HelpBox("無効な組み合わせです。削除して追加し直してください。", MessageType.Error);
                return;
            }
            DrawEyeProperties(eye);
            var separatePupil = eye.pupilMode == FBXUVEyePupilMode.Separate;
            var nextTab = GUILayout.Toolbar(editingTab, EditingTabLabels);
            if (nextTab != editingTab)
            {
                manualEditor.Reset();
                editingTab = nextTab;
                // The tabs draw different controls.
                GUIUtility.ExitGUI();
            }
            if (editingTab == 0) DrawRegionSelection(eye, separatePupil);
            else
            {
                Array.Clear(imageRects, 0, imageRects.Length);
                manualEditor.Draw(Layer, eye, Mathf.Max(280, (position.width - 65) / 2), ClearResults, Repaint);
            }
            if (separatePupil) DrawPupilAdjustment();
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("自動計測を確認")) Measure(eye);
                if (separatePupil && GUILayout.Button("補正をリセット"))
                {
                    RecordChange("瞳孔の補正をリセット", () => {
                        eye.pupilOffset = Vector2.zero;
                        eye.pupilScale = eye.pupilAspect = 1;
                    });
                    Measure(eye);
                }
            }
            measurement.Draw();
            EditorGUILayout.HelpBox("標準のメッシュから自動計算します。別パーツの瞳孔は縦横比と虹彩に対する投影面積比を保ちます。BlendShape・ポーズ・転送元Shaderの色やUV変形は計測に含めません。", MessageType.None);
        }

        internal void StoreIsland(int paneIndex, in FBXUVRegionPick pick)
        {
            var eye = CurrentEye();
            if (eye == null && EyeCount() == 0)
            {
                // A layer without eyes gets one with the first island picked, as 「目を追加」 would.
                AddEye();
                editingTab = 0;
                // The eye count is now in sync, so no refresh discards the other panes' pending meshes.
                syncedEyeCount = EyeCount();
                for (var index = 0; index < Panes.Length; index++) Panes[index].Sync(SavedRegion(index), FBXUVRefreshKind.PreservePending);
                eye = CurrentEye();
            }
            if (eye == null) return;
            var selection = new FBXUVEyeRegion { rendererPath = pick.RendererPath, region = FBXUVRegionFactory.Create(pick, AnalysisCache) };
            RecordChange("目のUV島を保存", () => {
                if (paneIndex == IrisPane) eye.iris = selection;
                else if (paneIndex == PupilPane) eye.pupil = selection;
                else eye.target = selection;
            });
            Panes[paneIndex].MarkSaved(FBXUVSavedRegion.From(selection.region, selection.rendererPath));
            ClearResults();
            Measure(eye);
        }

        private void DrawEyeSelection()
        {
            var labels = Layer.eyes == null ? Array.Empty<string>() : Layer.eyes.Select(e => e?.name ?? "未設定").ToArray();
            var next = selectedEye;
            var command = eyeList.DrawSelector(labels, ref next);
            if (command == FBXUVEntryCommand.Select) SelectEye(next);
            else if (command == FBXUVEntryCommand.Add) SelectEye(AddEye());
            else if (command == FBXUVEntryCommand.Remove)
            {
                RemoveEye(selectedEye);
                SelectEye(selectedEye);
            }
            else if (command != FBXUVEntryCommand.None)
            {
                MoveEye(selectedEye, selectedEye + (command == FBXUVEntryCommand.MoveUp ? -1 : 1));
                // The popup labels were laid out in the previous order.
                GUIUtility.ExitGUI();
            }
        }

        // Moves an eye in the list; the moved eye stays selected, so the panes keep their state.
        internal bool MoveEye(int index, int newIndex)
        {
            if (index < 0 || index >= EyeCount() || newIndex < 0 || newIndex >= EyeCount() || index == newIndex) return false;
            RecordChange("目の順番を変更", () => {
                var eye = Layer.eyes[index];
                Layer.eyes.RemoveAt(index);
                Layer.eyes.Insert(newIndex, eye);
            });
            if (index == selectedEye) selectedEye = newIndex;
            return true;
        }

        // Adds an eye with a unique name and selects it. Returns its index.
        internal int AddEye()
        {
            RecordChange("目の組み合わせを追加", () => {
                Layer.eyes ??= new List<FBXUVEyeBinding>();
                var number = Layer.eyes.Count + 1;
                var name = "Eye " + number;
                while (Layer.eyes.Any(e => FBXUVEntryListControl.NamesEqual(e?.name, name))) name = "Eye " + (++number);
                Layer.eyes.Add(new FBXUVEyeBinding { name = name });
            });
            eyeList.ClearNameError();
            selectedEye = Layer.eyes.Count - 1;
            return selectedEye;
        }

        // Removes an eye without asking (the list control confirms first) and selects the one that takes its place.
        internal bool RemoveEye(int index)
        {
            if (index < 0 || index >= EyeCount()) return false;
            RecordChange("目の組み合わせを削除", () => Layer.eyes.RemoveAt(index));
            eyeList.ClearNameError();
            selectedEye = Mathf.Clamp(index, 0, Mathf.Max(0, EyeCount() - 1));
            return true;
        }

        private bool ValidateEyeName(int index, string requestedName, out string normalized, out string error)
        {
            normalized = null;
            if (index < 0 || index >= EyeCount())
            {
                error = "変更対象の目が見つかりません。";
                return false;
            }
            var names = Layer.eyes.Select(e => e?.name).ToArray();
            return FBXUVEntryListControl.ValidateName("目", names, index, requestedName, out normalized, out error);
        }

        // Eye switches reload the saved values; the rest of this GUI pass was laid out for the previous eye.
        private void SelectEye(int index)
        {
            selectedEye = index;
            RequestRefresh(FBXUVRefreshKind.RestoreSaved);
            Repaint();
            GUIUtility.ExitGUI();
        }

        private void DrawEyeProperties(FBXUVEyeBinding eye)
        {
            var pupilMode = eye.pupilMode;
            using (var serialized = new SerializedObject(Layer))
            {
                var property = serialized.FindProperty("eyes").GetArrayElementAtIndex(selectedEye);
                // The new name is written to this SerializedObject so it is applied together with the other fields.
                eyeList.DrawEnabledAndName(property, selectedEye, (int index, string name, out string error) =>
                {
                    if (!ValidateEyeName(index, name, out var normalized, out error)) return false;
                    property.FindPropertyRelative("name").stringValue = normalized;
                    return true;
                });
                EditorGUILayout.PropertyField(property.FindPropertyRelative("transferMethod"), new GUIContent("補正方式"));
                transferMethodRect = GUILayoutUtility.GetLastRect();
                EditorGUILayout.PropertyField(property.FindPropertyRelative("pupilMode"), new GUIContent("瞳孔の構成"));
                pupilModeRect = GUILayoutUtility.GetLastRect();
                if (!serialized.ApplyModifiedProperties()) return;
            }
            RequestRefresh(FBXUVRefreshKind.PreservePending);
            Repaint();
            // The pupil pane appears or disappears with the pupil mode.
            if (eye.pupilMode != pupilMode) GUIUtility.ExitGUI();
        }

        private void DrawRegionSelection(FBXUVEyeBinding eye, bool separatePupil)
        {
            EditorGUILayout.HelpBox(separatePupil
                ? "同じ側の目の「虹彩」「別パーツの瞳孔」「転送先」を選択します。画像のクリックまたは「UV島」の選択で保存します。スクロールで拡大、中ボタンで移動。"
                : "「虹彩」と「転送先」を選択します。画像に描かれた瞳孔も含めて一体で転送します。瞳孔のない画像にも使用できます。瞳孔だけの位置・大きさ補正は行いません。", MessageType.Info);
            if (eye.transferMethod == FBXUVEyeTransferMethod.Topology)
                EditorGUILayout.HelpBox("面のつながりと形状から内部の対応を計算します。画像一体型では、両側の辺が一点に集まる頂点を検出できる場合、その点を瞳孔中心として合わせます。別パーツの瞳孔に対応する穴がある場合は、虹彩の内周も追従します。模様が変形するため、従来型と比較して選んでください。", MessageType.Info);
            var size = PreviewSize(position.width, separatePupil ? 3 : 2);
            using (new EditorGUILayout.HorizontalScope())
            {
                DrawPane(IrisPane, size);
                if (separatePupil)
                {
                    GUILayout.Space(16f);
                    DrawPane(PupilPane, size);
                }
                else imageRects[PupilPane] = default;
                GUILayout.Space(16f);
                DrawPane(TargetPane, size);
            }
        }

        private void DrawPane(int index, float size)
        {
            var pane = Panes[index];
            var isTarget = index == TargetPane;
            var targetTexture = ResolveTargetTexture();
            var candidates = isTarget
                ? CandidateCache.GetTarget(Layer, TargetModelOrPrefab, targetTexture, FBXUVCandidateGrouping.ByRenderer, out var error)
                : CandidateCache.GetSource(SourceModelOrPrefab, FBXUVCandidateGrouping.ByRenderer, out error);
            pane.Sync(SavedRegion(index), FBXUVRefreshKind.None);
            pane.TrySelectOnlyCandidate(candidates);
            var context = CreatePaneContext(
                candidates,
                error,
                isTarget,
                () => isTarget
                    ? FBXUVCandidateMessages.Target(Layer, targetTexture, candidates.Options.Count, pane.Mesh != null)
                    : FBXUVCandidateMessages.Source(SourceModelOrPrefab, candidates.Options.Count, pane.Mesh != null),
                isTarget ? targetTexture : Layer.sourceTexture,
                size);
            var pick = pane.Draw(context);
            imageRects[index] = pane.PreviewScreenRect;
            if (!pick.HasValue) return;
            StoreIsland(index, pick.Value);
        }

        private void DrawPupilAdjustment()
        {
            using (var serialized = new SerializedObject(Layer))
            {
                var property = serialized.FindProperty("eyes").GetArrayElementAtIndex(selectedEye);
                EditorGUILayout.LabelField("自動配置からの調整", EditorStyles.boldLabel);
                EditorGUILayout.PropertyField(property.FindPropertyRelative("pupilOffset"), new GUIContent("瞳孔の位置補正", "転送先の目の幅・高さを1とします。+Xはアバターの左、+Yは上です。"));
                EditorGUILayout.PropertyField(property.FindPropertyRelative("pupilScale"), new GUIContent("瞳孔の大きさ倍率", "1で自動計測値。縦横比を保って拡大・縮小します。"));
                EditorGUILayout.PropertyField(property.FindPropertyRelative("pupilAspect"), new GUIContent("瞳孔の縦横比倍率", "1で転送元の縦横比。面積を保ちながら幅÷高さを補正します。"));
                if (!serialized.ApplyModifiedProperties()) return;
            }
            RequestRefresh(FBXUVRefreshKind.PreservePending);
            Repaint();
        }

        internal void Measure(FBXUVEyeBinding eye)
        {
            try
            {
                var mapping = FBXUVEyeMapping.Create(Layer.sourceModelOrPrefab, Layer.targetModelOrPrefab, eye, new FBXUVMeshAnalysisCache());
                measurement = FBXUVMessage.Info(string.Join("\n", mapping.AllParts.Select((part, i) =>
                    (mapping.Parts == null ? "" : $"領域 {i + 1}: ") + Measurement(part, eye))));
            }
            catch (Exception e)
            {
                measurement = FBXUVMessage.Warning(e.Message);
            }
            resultsDirtyCount = EditorUtility.GetDirtyCount(Layer);
        }

        private static string Measurement(FBXUVEyeMapping mapping, FBXUVEyeBinding eye)
        {
            if (!mapping.HasPupil)
            {
                var targetCenter = mapping.TargetPupilCenter * .5f + Vector2.one * .5f;
                return mapping.HasTopologyPupilCenter
                    ? $"辺の収束点を瞳孔中心として対応付けました（各目の外接矩形内で0〜1）。\n転送元: ({mapping.MeasuredCenter.x:F4}, {mapping.MeasuredCenter.y:F4}) → 転送先: ({targetCenter.x:F4}, {targetCenter.y:F4})"
                    : eye.transferMethod == FBXUVEyeTransferMethod.Topology
                        ? "両側で一意の収束点を検出できないため、外周と面の形状から対応付けています。瞳孔中心は固定していません。"
                        : "画像一体型の設定は有効です。瞳孔の個別計測は行いません。";
            }
            return $"転送元の瞳孔中心: ({mapping.MeasuredCenter.x:F4}, {mapping.MeasuredCenter.y:F4})\n"
                + $"虹彩に対する幅・高さ: {mapping.MeasuredSize.x:P2} / {mapping.MeasuredSize.y:P2}　縦横比（幅÷高さ）: {mapping.MeasuredAspect:F4}";
        }

        // The measurement describes the previous configuration.
        private void ClearResults()
        {
            measurement = default;
        }

        private int EyeCount() => Layer == null || Layer.eyes == null ? 0 : Layer.eyes.Count;

        private FBXUVEyeBinding CurrentEye() => selectedEye >= 0 && selectedEye < EyeCount() ? Layer.eyes[selectedEye] : null;

        // The stored renderer path is passed exactly, including "" for the root renderer.
        private FBXUVSavedRegion SavedRegion(int paneIndex)
        {
            var eye = CurrentEye();
            var eyeRegion = eye == null ? null : paneIndex == IrisPane ? eye.iris : paneIndex == PupilPane ? eye.pupil : eye.target;
            return FBXUVSavedRegion.From(eyeRegion?.region, eyeRegion?.rendererPath);
        }
    }
}
