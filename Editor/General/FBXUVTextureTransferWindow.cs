using System;
using System.Collections.Generic;
using GokouKotori.FBXUVTextureTransfer;
using UnityEditor;
using UnityEngine;
using Unity.Profiling;

namespace GokouKotori.FBXUVTextureTransfer.Editor
{
    // UV Region Editor: adds / removes / renames Regions, switches the active one and writes the picked islands into regionBindings.
    public sealed class FBXUVTextureTransferWindow : FBXUVLayerWindow<FBXUVTextureTransferLayer>
    {
        private static readonly ProfilerMarker WindowGuiMarker =
            new ProfilerMarker("FBXUVTextureTransfer.EditorWindow.OnGUI");

        private const string WindowTitle = "FBX UV Texture Transfer";
        private const string NoRegionMessage = "「Regionを追加」でRegionを追加してから、UV島を選択してください。";
        private const string MissingBindingMessage = "選択中のregionがLayerにありません。";
        private static readonly GUIContent OrientationLabel =
            new GUIContent("向き補正", "転送元画像を転送先の領域へ配置する向きを手動で指定します。");
        private static readonly GUIContent[] OrientationOptions =
            Array.ConvertAll(FBXUVTextureTransferLayerEditor.OrientationLabels, label => new GUIContent(label));

        [SerializeField] private string activeRegionName = string.Empty;
        [SerializeField] private int activeRegionIndex = -1;
        [NonSerialized] private SerializedObject serializedLayer;
        [NonSerialized] private FBXUVRegionPickerPane sourcePane;
        [NonSerialized] private FBXUVRegionPickerPane targetPane;
        [NonSerialized] private int layerDirtyCount = -1;
        [NonSerialized] private bool regionNameLabelsValid;
        [NonSerialized] private string[] regionNameLabels;
        [NonSerialized] private FBXUVEntryListControl regionList;

        // Created lazily: the base analysis cache is not initialized yet while derived fields are.
        internal FBXUVRegionPickerPane SourcePane => sourcePane ??= new FBXUVRegionPickerPane("転送元：UV", AnalysisCache);
        internal FBXUVRegionPickerPane TargetPane => targetPane ??= new FBXUVRegionPickerPane("転送先：UV", AnalysisCache);
        internal FBXUVEntryListControl RegionList =>
            regionList ??= new FBXUVEntryListControl("Region", "転送元／転送先のUV島の選択も削除されます。");
        internal int ActiveRegionIndex => activeRegionIndex;
        internal string ActiveRegionName => activeRegionName;

        protected override string MissingLayerMessage =>
            "FBXUVTextureTransferLayer を指定してください。HierarchyでLayerを選択しても開けます。";

        [MenuItem("Tools/Gokoukotori/FBX UV Texture Transfer/UV Region Editor")]
        public static void Open()
        {
            GetWindow<FBXUVTextureTransferWindow>(WindowTitle);
        }

        public static void Open(FBXUVTextureTransferLayer targetLayer)
        {
            var window = GetWindow<FBXUVTextureTransferWindow>(WindowTitle);
            window.SetLayer(targetLayer);
            window.Show();
            window.Focus();
        }

        protected override void OnEnable()
        {
            base.OnEnable();
            minSize = new Vector2(760f, 620f);
            RegisterPanes(SourcePane, TargetPane);
        }

        protected override void OnLayerChanged()
        {
            serializedLayer = null;
            layerDirtyCount = -1;
            regionNameLabelsValid = false;
            RegionList.ClearNameError();
        }

        internal override void OnRefresh(FBXUVRefreshKind kind)
        {
            regionNameLabelsValid = false;
            var currentSerializedLayer = GetSerializedLayer();
            if (currentSerializedLayer == null)
            {
                activeRegionIndex = -1;
                activeRegionName = string.Empty;
                SyncPanes(FBXUVRefreshKind.RestoreSaved);
                return;
            }

            Reconcile(currentSerializedLayer, kind);
        }

        // Edits made outside this window (Debug Inspector, scripts, other windows) mark the layer dirty.
        // UpdateIfRequiredOrScript() returns true on every call for a MonoBehaviour, so the dirty count decides.
        // The dirty count also refreshes the Region name labels after renames / reorders, which a comparison of the
        // saved regions cannot see; in-place region edits still reach the panes on the next GUI event via Sync(saved, None).
        protected override bool DetectSavedValueChanges()
        {
            var currentSerializedLayer = GetSerializedLayer();
            if (currentSerializedLayer == null) return false;

            currentSerializedLayer.UpdateIfRequiredOrScript();
            var dirtyCount = EditorUtility.GetDirtyCount(Layer);
            if (dirtyCount == layerDirtyCount) return false;

            layerDirtyCount = dirtyCount;
            regionNameLabelsValid = false;
            return true;
        }

        protected override void DrawContent()
        {
            using (WindowGuiMarker.Auto())
            {
                var currentSerializedLayer = GetSerializedLayer();
                if (currentSerializedLayer == null) return;

                ReconcileActiveRegion(currentSerializedLayer);
                DrawRegionSelection(currentSerializedLayer);
                EditorGUILayout.Space(8f);
                var size = PreviewSize(position.width, 2);
                var targetTexture = ResolveTargetTexture();
                using (new EditorGUILayout.HorizontalScope())
                {
                    DrawPane(true, size, targetTexture);
                    GUILayout.Space(16f);
                    DrawPane(false, size, targetTexture);
                }
            }
        }

        internal void SetActiveRegion(int index, string name)
        {
            activeRegionIndex = index;
            activeRegionName = name ?? string.Empty;
        }

        internal SerializedObject GetSerializedLayer()
        {
            if (Layer == null)
            {
                serializedLayer = null;
                return null;
            }

            if (serializedLayer == null || serializedLayer.targetObject != Layer)
            {
                serializedLayer = new SerializedObject(Layer);
            }

            return serializedLayer;
        }

        internal List<FBXUVMeshOption> CollectSourceOptions()
        {
            return SourceSnapshot(out _).Options;
        }

        internal List<FBXUVMeshOption> CollectTargetOptions()
        {
            return TargetSnapshot(ResolveTargetTexture(), out _).Options;
        }

        // forceRefresh reloads the saved values into both panes and discards pending selections.
        internal bool ReconcileActiveRegion(SerializedObject serializedLayer, bool forceRefresh = false)
        {
            return Reconcile(serializedLayer, forceRefresh ? FBXUVRefreshKind.RestoreSaved : FBXUVRefreshKind.None);
        }

        internal void StoreIsland(bool isSource, in FBXUVRegionPick pick)
        {
            if (!isSource && !FBXUVCanvasHierarchyUtility.TryFindCanvas(Layer, out _, out _, out _)) return;

            var currentSerializedLayer = GetSerializedLayer();
            var binding = currentSerializedLayer == null ? null : GetActiveBinding(currentSerializedLayer);
            if (binding == null)
            {
                Notify(MissingBindingMessage);
                return;
            }

            var property = binding.FindPropertyRelative(isSource ? "sourceRegion" : "targetRegion");
            if (property == null) return;

            var region = FBXUVRegionFactory.Create(pick, AnalysisCache);
            Undo.RecordObject(currentSerializedLayer.targetObject, "UV島を選択");
            property.FindPropertyRelative("mesh").objectReferenceValue = region.Mesh;
            property.FindPropertyRelative("subMeshIndex").intValue = region.SubMeshIndex;
            property.FindPropertyRelative("uvChannel").intValue = region.UvChannel;
            property.FindPropertyRelative("islandId").intValue = region.IslandId;
            property.FindPropertyRelative("meshHash").stringValue = region.MeshHash;

            var bounds = property.FindPropertyRelative("bounds");
            bounds.FindPropertyRelative("minU").floatValue = region.Bounds.MinU;
            bounds.FindPropertyRelative("minV").floatValue = region.Bounds.MinV;
            bounds.FindPropertyRelative("maxU").floatValue = region.Bounds.MaxU;
            bounds.FindPropertyRelative("maxV").floatValue = region.Bounds.MaxV;

            var triangles = property.FindPropertyRelative("triangles");
            triangles.arraySize = region.Triangles.Count;
            for (var index = 0; index < region.Triangles.Count; index++)
            {
                var source = region.Triangles[index];
                var destination = triangles.GetArrayElementAtIndex(index);
                destination.FindPropertyRelative("index").intValue = source.Index;
                destination.FindPropertyRelative("a").vector2Value = source.A;
                destination.FindPropertyRelative("b").vector2Value = source.B;
                destination.FindPropertyRelative("c").vector2Value = source.C;
            }

            currentSerializedLayer.ApplyModifiedProperties();
            PrefabUtility.RecordPrefabInstancePropertyModifications(currentSerializedLayer.targetObject);
            EditorUtility.SetDirty(currentSerializedLayer.targetObject);
            (isSource ? SourcePane : TargetPane).MarkSaved(FBXUVSavedRegion.From(region));
            Repaint();
        }

        private void DrawRegionSelection(SerializedObject serializedLayer)
        {
            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField("UV島の対応付け", EditorStyles.boldLabel);
            var selected = activeRegionIndex;
            var command = RegionList.DrawSelector(CollectRegionNames(serializedLayer), ref selected);
            if (command != FBXUVEntryCommand.None)
            {
                if (command == FBXUVEntryCommand.Select) SelectRegion(serializedLayer, selected);
                else if (command == FBXUVEntryCommand.Add) AddRegion();
                else if (command == FBXUVEntryCommand.Remove) RemoveRegion(activeRegionIndex);
                else MoveRegion(activeRegionIndex, activeRegionIndex + (command == FBXUVEntryCommand.MoveUp ? -1 : 1));
                // The panes' controls were laid out for the previous Region.
                GUIUtility.ExitGUI();
            }

            var binding = GetActiveBinding(serializedLayer);
            if (binding == null) return;

            RegionList.DrawEnabledAndName(binding, activeRegionIndex, RenameRegion);
            var orientation = binding.FindPropertyRelative("orientation");
            EditorGUI.BeginChangeCheck();
            var orientationIndex = EditorGUILayout.Popup(
                OrientationLabel,
                FBXUVTextureTransferLayerEditor.OrientationPopupIndex(orientation.intValue),
                OrientationOptions);
            if (EditorGUI.EndChangeCheck())
            {
                orientation.intValue = (int)FBXUVTextureTransferLayerEditor.OrientationValues[orientationIndex];
            }

            FBXUVRegionBindingSerializedPropertyUtility.ApplyModifiedProperties(serializedLayer);
        }

        private void SelectRegion(SerializedObject serializedLayer, int index)
        {
            activeRegionIndex = index;
            activeRegionName = GetActiveBinding(serializedLayer)?
                .FindPropertyRelative("name")?.stringValue ?? string.Empty;
            SyncPanes(FBXUVRefreshKind.RestoreSaved);
        }

        // Adds a Region with a unique name and makes it the active one. Returns its index, or -1 without a layer.
        internal int AddRegion()
        {
            var currentSerializedLayer = GetSerializedLayer();
            if (currentSerializedLayer == null) return -1;

            currentSerializedLayer.Update();
            var index = FBXUVRegionBindingSerializedPropertyUtility.Add(
                FBXUVRegionBindingSerializedPropertyUtility.FindBindings(currentSerializedLayer));
            if (index < 0) return -1;

            ApplyRegionListChange(currentSerializedLayer, "Regionを追加");
            SelectRegion(currentSerializedLayer, index);
            return index;
        }

        // Removes a Region without asking (the list control confirms first) and selects the one that takes its place.
        internal bool RemoveRegion(int index)
        {
            var currentSerializedLayer = GetSerializedLayer();
            if (currentSerializedLayer == null) return false;

            currentSerializedLayer.Update();
            var bindings = FBXUVRegionBindingSerializedPropertyUtility.FindBindings(currentSerializedLayer);
            if (!FBXUVRegionBindingSerializedPropertyUtility.DeleteAt(bindings, index)) return false;

            ApplyRegionListChange(currentSerializedLayer, "Regionを削除");
            SelectRegion(currentSerializedLayer, Mathf.Min(index, bindings.arraySize - 1));
            return true;
        }

        // Moves a Region in the composition order (top first). The moved Region stays active and keeps the pending picks.
        internal bool MoveRegion(int index, int newIndex)
        {
            var currentSerializedLayer = GetSerializedLayer();
            if (currentSerializedLayer == null) return false;

            currentSerializedLayer.Update();
            var bindings = FBXUVRegionBindingSerializedPropertyUtility.FindBindings(currentSerializedLayer);
            if (!FBXUVRegionBindingSerializedPropertyUtility.Move(bindings, index, newIndex)) return false;

            ApplyRegionListChange(currentSerializedLayer, "Regionの順番を変更");
            if (index == activeRegionIndex) activeRegionIndex = newIndex;
            return true;
        }

        internal bool RenameRegion(int index, string requestedName, out string error)
        {
            var currentSerializedLayer = GetSerializedLayer();
            if (currentSerializedLayer == null)
            {
                error = MissingBindingMessage;
                return false;
            }

            // No Update(): renames arrive mid-draw, and the same pass may hold an unapplied enabled / orientation edit.
            var bindings = FBXUVRegionBindingSerializedPropertyUtility.FindBindings(currentSerializedLayer);
            if (!FBXUVRegionBindingSerializedPropertyUtility.TryRename(bindings, index, requestedName, out error)) return false;

            ApplyRegionListChange(currentSerializedLayer, "Region名を変更");
            // The active Region is reconciled by name.
            if (index == activeRegionIndex)
            {
                activeRegionName = bindings.GetArrayElementAtIndex(index).FindPropertyRelative("name").stringValue;
            }

            return true;
        }

        private void ApplyRegionListChange(SerializedObject currentSerializedLayer, string undoLabel)
        {
            FBXUVRegionBindingSerializedPropertyUtility.ApplyModifiedProperties(currentSerializedLayer);
            Undo.SetCurrentGroupName(undoLabel);
            regionNameLabelsValid = false;
            RegionList.ClearNameError();
            Repaint();
        }

        private void DrawPane(bool isSource, float size, Texture targetTexture)
        {
            var pane = isSource ? SourcePane : TargetPane;
            string error;
            var candidates = isSource ? SourceSnapshot(out error) : TargetSnapshot(targetTexture, out error);
            pane.Sync(SavedRegion(isSource), FBXUVRefreshKind.None);
            pane.TrySelectOnlyCandidate(candidates);
            var context = CreatePaneContext(
                candidates,
                error,
                !isSource,
                () => isSource
                    ? FBXUVCandidateMessages.Source(SourceModelOrPrefab, candidates.Options.Count, pane.Mesh != null)
                    : FBXUVCandidateMessages.Target(Layer, targetTexture, candidates.Options.Count, pane.Mesh != null),
                isSource ? Layer.DefaultSourceTexture : targetTexture,
                size);
            context.HandleInput = HandleClickWithoutRegion;

            var pick = pane.Draw(context);
            if (!pick.HasValue) return;

            if (activeRegionIndex < 0)
            {
                Notify(NoRegionMessage);
                return;
            }

            StoreIsland(isSource, pick.Value);
        }

        private bool HandleClickWithoutRegion(Rect local, Rect image)
        {
            if (activeRegionIndex >= 0) return false;

            var current = Event.current;
            if (current.type != EventType.MouseDown || current.button != 0 || !local.Contains(current.mousePosition))
            {
                return false;
            }

            Notify(NoRegionMessage);
            current.Use();
            return true;
        }

        private FBXUVMeshCandidateCache.Snapshot SourceSnapshot(out string error)
        {
            return CandidateCache.GetSource(SourceModelOrPrefab, FBXUVCandidateGrouping.ByMesh, out error);
        }

        private FBXUVMeshCandidateCache.Snapshot TargetSnapshot(Texture targetTexture, out string error)
        {
            return CandidateCache.GetTarget(
                Layer,
                TargetModelOrPrefab,
                targetTexture,
                FBXUVCandidateGrouping.ByMesh,
                out error);
        }

        private bool Reconcile(SerializedObject serializedLayer, FBXUVRefreshKind kind)
        {
            var bindings = serializedLayer.FindProperty("regionBindings");
            if (bindings == null || !bindings.isArray || bindings.arraySize == 0)
            {
                var changed = activeRegionIndex != -1 || !string.IsNullOrEmpty(activeRegionName);
                activeRegionIndex = -1;
                activeRegionName = string.Empty;
                if (changed) SyncPanes(FBXUVRefreshKind.RestoreSaved);
                else if (kind != FBXUVRefreshKind.None) SyncPanes(kind);

                return changed;
            }

            var reconciledIndex = -1;
            if (!string.IsNullOrEmpty(activeRegionName))
            {
                for (var index = 0; index < bindings.arraySize; index++)
                {
                    var candidateName = bindings.GetArrayElementAtIndex(index)
                        .FindPropertyRelative("name")?.stringValue;
                    if (NamesEqual(candidateName, activeRegionName))
                    {
                        reconciledIndex = index;
                        break;
                    }
                }
            }

            if (reconciledIndex < 0)
            {
                reconciledIndex = activeRegionIndex < 0
                    ? 0
                    : Mathf.Clamp(activeRegionIndex, 0, bindings.arraySize - 1);
            }

            var reconciledName = bindings.GetArrayElementAtIndex(reconciledIndex)
                .FindPropertyRelative("name")?.stringValue ?? string.Empty;
            var selectionChanged = activeRegionIndex != reconciledIndex ||
                                   !string.Equals(activeRegionName, reconciledName, StringComparison.Ordinal);
            activeRegionIndex = reconciledIndex;
            activeRegionName = reconciledName;
            if (selectionChanged)
            {
                SyncPanes(FBXUVRefreshKind.RestoreSaved);
            }
            else if (kind != FBXUVRefreshKind.None)
            {
                SyncPanes(kind);
            }

            return selectionChanged;
        }

        private void SyncPanes(FBXUVRefreshKind kind)
        {
            SourcePane.Sync(SavedRegion(true), kind);
            TargetPane.Sync(SavedRegion(false), kind);
        }

        private FBXUVSavedRegion SavedRegion(bool isSource)
        {
            var bindings = Layer == null ? null : Layer.RegionBindings;
            if (bindings == null || activeRegionIndex < 0 || activeRegionIndex >= bindings.Count)
            {
                return FBXUVSavedRegion.Empty;
            }

            var binding = bindings[activeRegionIndex];
            if (binding == null) return FBXUVSavedRegion.Empty;

            return FBXUVSavedRegion.From(isSource ? binding.SourceRegion : binding.TargetRegion);
        }

        private SerializedProperty GetActiveBinding(SerializedObject serializedLayer)
        {
            var bindings = serializedLayer.FindProperty("regionBindings");
            return bindings != null && bindings.isArray &&
                   activeRegionIndex >= 0 && activeRegionIndex < bindings.arraySize
                ? bindings.GetArrayElementAtIndex(activeRegionIndex)
                : null;
        }

        private string[] CollectRegionNames(SerializedObject serializedLayer)
        {
            if (regionNameLabelsValid) return regionNameLabels;

            var bindings = serializedLayer.FindProperty("regionBindings");
            if (bindings == null || !bindings.isArray)
            {
                regionNameLabels = Array.Empty<string>();
                regionNameLabelsValid = true;
                return regionNameLabels;
            }

            regionNameLabels = new string[bindings.arraySize];
            for (var index = 0; index < bindings.arraySize; index++)
            {
                var name = bindings.GetArrayElementAtIndex(index)
                    .FindPropertyRelative("name")?.stringValue?.Trim();
                regionNameLabels[index] = string.IsNullOrEmpty(name) ? $"Region {index + 1}" : name;
            }

            regionNameLabelsValid = true;
            return regionNameLabels;
        }

        private static bool NamesEqual(string first, string second)
        {
            return string.Equals(first?.Trim(), second?.Trim(), StringComparison.OrdinalIgnoreCase);
        }
    }
}
