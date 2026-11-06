using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace GokouKotori.FBXUVTextureTransfer.Editor
{
    // Filled by the owning window before every Draw call.
    internal sealed class FBXUVPaneContext
    {
        internal FBXUVMeshCandidateCache.Snapshot Candidates;
        internal string CandidateError;
        internal Func<string> ReplacementWarning;
        internal Texture Background;
        internal Texture OverlayTexture;
        internal bool InputEnabled = true;
        internal bool IslandSelectionEnabled = true;
        internal Action<Rect> DrawOverlay;
        // (local preview rect, image rect) -> true when the event was consumed before the island hit test.
        internal Func<Rect, Rect, bool> HandleInput;
        internal float Size;
        // Required: the event's mouse position captured at the start of the window's OnGUI, before any clip.
        // It anchors PreviewScreenRect exactly in window coordinates.
        internal Vector2 WindowMousePosition;
        internal Action<string> Notify;
        internal Action Repaint;
    }

    // One preview pane: the working mesh / UV selection, its islands and the zoomable preview.
    // The pane never writes layer data. Draw returns a pick; the window saves it and calls MarkSaved.
    internal sealed class FBXUVRegionPickerPane
    {
        private const string MeshSelectionLabel = "メッシュ / サブメッシュ";
        private const string NoIslandAtPositionMessage = "この位置にUV島はありません。";
        private const string NotInCandidatesMessage = "現在のModel / Prefab候補からメッシュを選択してください。";
        private const string PendingMessage = "未確定：島をクリックまたは選択して保存してください";
        private const string HashMismatchMessage = "メッシュ内容が保存時と異なります。UV島を選び直してください。";
        private static readonly Color backgroundColor = new Color(.12f, .12f, .12f);

        private readonly string title;
        private FBXUVMeshAnalysisCache cache;
        private List<FBXUVIsland> islands;
        private List<FBXUVIsland> islandLabelSource;
        private string[] islandLabels;
        private bool hashComputed;
        private Mesh hashMesh;
        private int hashSubMeshIndex;
        private int hashUvChannel;
        private string currentHash;

        internal FBXUVRegionPickerPane(string title, FBXUVMeshAnalysisCache cache)
        {
            this.title = title;
            this.cache = cache;
        }

        internal Mesh Mesh { get; private set; }
        internal int SubMeshIndex { get; private set; }
        internal int UvChannel { get; private set; }
        internal string RendererPath { get; private set; }
        internal bool HasPendingSelection { get; private set; }
        internal IReadOnlyList<FBXUVIsland> Islands => islands;
        internal string Error { get; private set; }
        internal FBXUVSavedRegion Saved { get; private set; } = FBXUVSavedRegion.Empty;
        internal FBXUVPreviewViewport Viewport { get; private set; } = new FBXUVPreviewViewport();

        // Both rects are relative to the window origin and refreshed on Repaint.
        internal Rect PreviewScreenRect { get; private set; }
        internal Rect LastImageRect { get; private set; }

        internal bool IsSavedIslandCurrent =>
            MatchesSavedMesh()
            && MatchesSavedPath()
            && IsHashCurrent(Saved.MeshHash);

        // A failed hash computation also counts as a mismatch.
        internal bool HasHashMismatch => MatchesSavedMesh() && !IsHashCurrent(Saved.MeshHash);

        private int HighlightedIslandId => IsSavedIslandCurrent ? Saved.IslandId : -1;

        // Islands are reloaded by the next Sync: the window always refreshes after replacing its cache,
        // and the new cache stays empty until a pane actually needs a mesh.
        internal void SetAnalysisCache(FBXUVMeshAnalysisCache cache)
        {
            this.cache = cache;
            InvalidateHash();
        }

        internal bool Sync(FBXUVSavedRegion saved, FBXUVRefreshKind kind)
        {
            if (kind == FBXUVRefreshKind.None && saved.Equals(Saved)) return false;

            Saved = saved;
            if (kind == FBXUVRefreshKind.RestoreSaved) HasPendingSelection = false;
            if (!HasPendingSelection)
            {
                Mesh = saved.Mesh;
                SubMeshIndex = saved.SubMeshIndex;
                UvChannel = saved.UvChannel;
                RendererPath = saved.RendererPath;
            }

            InvalidateHash();
            ReloadIslands();
            return true;
        }

        internal void Select(FBXUVMeshOption option)
        {
            Mesh = option.Mesh;
            SubMeshIndex = option.SubMeshIndex;
            RendererPath = option.RendererPath;
            MarkPending();
        }

        internal void SetUvChannel(int uv)
        {
            UvChannel = Mathf.Clamp(uv, 0, 7);
            MarkPending();
        }

        internal bool TrySelectOnlyCandidate(FBXUVMeshCandidateCache.Snapshot candidates)
        {
            if (candidates == null || candidates.Options.Count != 1 || IndexIn(candidates) == 0) return false;

            Select(candidates.Options[0]);
            return true;
        }

        // Only a null renderer path (by-mesh windows) matches any renderer; "" is the Eye root renderer.
        internal int IndexIn(FBXUVMeshCandidateCache.Snapshot candidates)
        {
            return candidates == null ? -1 : candidates.IndexOf(Mesh, SubMeshIndex, RendererPath);
        }

        internal FBXUVRegionPick? PickIsland(int islandId)
        {
            if (islands == null) return null;

            foreach (var island in islands)
            {
                if (island.id == islandId) return new FBXUVRegionPick(Mesh, SubMeshIndex, UvChannel, RendererPath, island);
            }

            return null;
        }

        internal void MarkSaved(FBXUVSavedRegion saved)
        {
            Saved = saved;
            HasPendingSelection = false;
        }

        internal FBXUVRegionPick? Draw(FBXUVPaneContext context)
        {
            FBXUVRegionPick? pick = null;
            using (new EditorGUILayout.VerticalScope(GUILayout.Width(context.Size + 16f)))
            {
                EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
                bool replacementShown;
                // DisabledScope only greys the controls; the preview click is gated explicitly below.
                using (new EditorGUI.DisabledScope(!context.InputEnabled))
                {
                    replacementShown = DrawMeshSelection(context);
                    DrawUvSlider();
                    if (context.IslandSelectionEnabled) pick = DrawIslandPopup();
                }

                var clicked = DrawPreview(context);
                if (clicked.HasValue) pick = clicked;
                // A saved mesh outside the current candidates is kept until a candidate is chosen explicitly.
                if (pick.HasValue && IndexIn(context.Candidates) < 0)
                {
                    context.Notify?.Invoke(NotInCandidatesMessage);
                    pick = null;
                }

                if (Viewport.DrawControls(FindIsland(HighlightedIslandId))) context.Repaint?.Invoke();
                DrawStatus(context, replacementShown);
            }

            return pick;
        }

        // Returns true when the field showed its own out-of-candidates warning.
        private bool DrawMeshSelection(FBXUVPaneContext context)
        {
            var candidates = context.Candidates;
            if (candidates == null) return false;

            var selectedIndex = IndexIn(candidates);
            var warning = context.ReplacementWarning ?? (() => context.CandidateError ?? string.Empty);
            if (FBXUVMeshSelectionField.Draw(
                    candidates,
                    selectedIndex,
                    MeshSelectionLabel,
                    MeshSelectionLabel,
                    Mesh,
                    SubMeshIndex,
                    warning,
                    out var selection))
            {
                Select(selection);
            }

            return selectedIndex < 0;
        }

        private void DrawUvSlider()
        {
            using (new EditorGUI.DisabledScope(Mesh == null))
            {
                EditorGUI.BeginChangeCheck();
                var next = EditorGUILayout.IntSlider("UVチャンネル", UvChannel, 0, 7);
                if (EditorGUI.EndChangeCheck()) SetUvChannel(next);
            }
        }

        private FBXUVRegionPick? DrawIslandPopup()
        {
            if (islands == null || islands.Count == 0) return null;

            var highlighted = HighlightedIslandId;
            var current = highlighted < 0 ? 0 : islands.FindIndex(island => island.id == highlighted) + 1;
            EditorGUI.BeginChangeCheck();
            var next = EditorGUILayout.Popup("UV島", current, IslandLabels());
            if (!EditorGUI.EndChangeCheck() || next <= 0) return null;

            return PickIsland(islands[next - 1].id);
        }

        private string[] IslandLabels()
        {
            if (ReferenceEquals(islandLabelSource, islands)) return islandLabels;

            islandLabelSource = islands;
            islandLabels = new string[islands.Count + 1];
            islandLabels[0] = "<選択してください>";
            for (var index = 0; index < islands.Count; index++)
            {
                var island = islands[index];
                islandLabels[index + 1] = $"島 {island.id}（{island.triangles.Count}三角形）";
            }

            return islandLabels;
        }

        private FBXUVRegionPick? DrawPreview(FBXUVPaneContext context)
        {
            var rect = GUILayoutUtility.GetRect(context.Size, context.Size, GUILayout.ExpandWidth(false));
            var repaint = Event.current.type == EventType.Repaint;
            if (repaint)
            {
                // The clip transform of the mouse position is exact, so it anchors the rect in window coordinates.
                var position = context.WindowMousePosition - Event.current.mousePosition + rect.position;
                PreviewScreenRect = new Rect(position, rect.size);
            }

            GUI.BeginGroup(rect);
            var local = new Rect(Vector2.zero, rect.size);
            if (Viewport.HandleInput(local)) context.Repaint?.Invoke();
            var image = Viewport.ImageRect(local);
            if (repaint) LastImageRect = new Rect(PreviewScreenRect.position + image.position, image.size);
            EditorGUI.DrawRect(local, backgroundColor);
            if (context.Background != null)
            {
                EditorGUI.DrawPreviewTexture(image, context.Background, null, ScaleMode.StretchToFill);
            }

            if (context.OverlayTexture != null)
            {
                GUI.DrawTexture(image, context.OverlayTexture, ScaleMode.StretchToFill, true);
            }

            if (repaint)
            {
                Handles.BeginGUI();
                if (context.IslandSelectionEnabled) FBXUVIslandPreview.Draw(image, islands, HighlightedIslandId);
                context.DrawOverlay?.Invoke(image);
                Handles.EndGUI();
            }

            FBXUVRegionPick? pick = null;
            var consumed = context.HandleInput != null && context.HandleInput(local, image);
            if (!consumed && context.InputEnabled && context.IslandSelectionEnabled)
            {
                pick = HandleClick(local, image, context);
            }

            GUI.EndGroup();
            return pick;
        }

        private FBXUVRegionPick? HandleClick(Rect local, Rect image, FBXUVPaneContext context)
        {
            var current = Event.current;
            if (current.type != EventType.MouseDown || current.button != 0 || !local.Contains(current.mousePosition))
            {
                return null;
            }

            current.Use();
            // No usable mesh (or an extraction error) reports the same as a click beside every island.
            var island = islands == null
                ? null
                : FBXUVIslandExtractor.HitTest(islands, FBXUVPreviewViewport.ToUv(image, current.mousePosition));
            if (island == null)
            {
                context.Notify?.Invoke(NoIslandAtPositionMessage);
                return null;
            }

            context.Repaint?.Invoke();
            return new FBXUVRegionPick(Mesh, SubMeshIndex, UvChannel, RendererPath, island);
        }

        private void DrawStatus(FBXUVPaneContext context, bool replacementShown)
        {
            EditorGUILayout.LabelField(StatusText(context.Candidates), EditorStyles.wordWrappedLabel);
            if (HasPendingSelection) EditorGUILayout.HelpBox(PendingMessage, MessageType.Info);
            if (HasHashMismatch) EditorGUILayout.HelpBox(HashMismatchMessage, MessageType.Warning);
            if (!string.IsNullOrEmpty(Error))
            {
                EditorGUILayout.HelpBox(Error, MessageType.Warning);
            }
            else if (islands != null && islands.Count == 0)
            {
                EditorGUILayout.HelpBox(FBXUVCandidateMessages.NoIslands, MessageType.Warning);
            }

            // The selection field already shows the candidate warning when the saved mesh is outside the candidates.
            if (!replacementShown && !string.IsNullOrEmpty(context.CandidateError))
            {
                EditorGUILayout.HelpBox(context.CandidateError, MessageType.Warning);
            }
        }

        private string StatusText(FBXUVMeshCandidateCache.Snapshot candidates)
        {
            if (!Saved.HasMesh || Saved.IslandId < 0) return "未選択";

            return $"保存済み：{SavedLabel(candidates)} / サブメッシュ {Saved.SubMeshIndex} / UV {Saved.UvChannel} / 島 {Saved.IslandId}";
        }

        private string SavedLabel(FBXUVMeshCandidateCache.Snapshot candidates)
        {
            var index = candidates == null
                ? -1
                : candidates.IndexOf(Saved.Mesh, Saved.SubMeshIndex, Saved.RendererPath);
            if (index < 0) return string.IsNullOrEmpty(Saved.RendererPath) ? Saved.Mesh.name : Saved.RendererPath;

            var label = candidates.Options[index].Label;
            var end = label.IndexOf(" / サブメッシュ", StringComparison.Ordinal);
            return end < 0 ? label : label.Substring(0, end);
        }

        private FBXUVIsland FindIsland(int islandId)
        {
            return islandId < 0 || islands == null ? null : islands.Find(island => island.id == islandId);
        }

        private bool MatchesSavedMesh()
        {
            return Saved.HasMesh
                && Mesh == Saved.Mesh
                && SubMeshIndex == Saved.SubMeshIndex
                && UvChannel == Saved.UvChannel;
        }

        // A saved region without renderer information matches by mesh / submesh only.
        private bool MatchesSavedPath()
        {
            return Saved.RendererPath == null
                || string.Equals(Saved.RendererPath, RendererPath, StringComparison.Ordinal);
        }

        private bool IsHashCurrent(string storedHash)
        {
            if (string.IsNullOrEmpty(storedHash)) return false;

            var hash = CurrentHash();
            return hash != null && string.Equals(storedHash, hash, StringComparison.Ordinal);
        }

        // Cached per (mesh, submesh, uv) so the status and highlight do not hash every frame.
        private string CurrentHash()
        {
            if (hashComputed
                && ReferenceEquals(hashMesh, Mesh)
                && hashSubMeshIndex == SubMeshIndex
                && hashUvChannel == UvChannel)
            {
                return currentHash;
            }

            hashComputed = true;
            hashMesh = Mesh;
            hashSubMeshIndex = SubMeshIndex;
            hashUvChannel = UvChannel;
            currentHash = null;
            if (Mesh == null) return null;

            try
            {
                currentHash = cache.Get(Mesh, SubMeshIndex, UvChannel).GetContentHash();
            }
            catch (Exception)
            {
                currentHash = null;
            }

            return currentHash;
        }

        private void InvalidateHash()
        {
            hashComputed = false;
            hashMesh = null;
            currentHash = null;
        }

        private void MarkPending()
        {
            HasPendingSelection = true;
            ReloadIslands();
        }

        private void ReloadIslands()
        {
            islands = null;
            Error = null;
            if (Mesh == null) return;

            try
            {
                islands = cache.Get(Mesh, SubMeshIndex, UvChannel).GetIslands();
            }
            catch (Exception exception)
            {
                Error = exception.Message;
            }
        }
    }
}
