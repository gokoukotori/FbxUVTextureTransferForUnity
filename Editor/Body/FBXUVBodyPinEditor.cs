using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace GokouKotori.FBXUVTextureTransfer.Editor
{
    // Body correction pins (spec §6.2) with the eye manual editor's interaction model: a tool and a click add a pin, a drag
    // previews a draft and mouse up commits it (one Undo step), Esc / focus loss / layer switch cancel the drag. A move that
    // cannot be applied is shown as 「未反映」 with its reason while the last applied preview stays (spec §5.2).
    // The correspondence entry is cached and shared by every layer of the avatar pair, so it is obtained again for every
    // render (ruling T8-3), except that one drag keeps the entry it started with while the cache keeps it (ruling T12-4);
    // this editor otherwise keeps only its own preview textures and the outline lines derived from the entry.
    internal sealed class FBXUVBodyPinEditor : IDisposable
    {
        internal const int MaxPreviewResolution = 1024;
        internal const float MinRadiusCentimeters = .5f;
        internal const float MaxRadiusCentimeters = 30f;
        private const float PinHitPixels = 10f;
        private const int ControlHint = 72311;
        private static readonly string[] toolLabels = FBXUVManualCorrectionLabels.Tools;
        private static readonly Color32 invalidColor = new Color32(255, 0, 255, 200);
        private static readonly Color32 sharedColor = new Color32(255, 230, 0, 200);
        private static readonly Color outlineColor = new Color(.1f, .85f, 1, .6f);
        private static readonly Color highlightColor = new Color(1, .65f, .15f, .95f);
        private static readonly Color backgroundColor = new Color(.16f, .16f, .16f);

        private readonly FBXUVPreviewViewport viewport = new FBXUVPreviewViewport();
        private FBXUVBodyOverlayTransferLayer layer;
        private Action<string, Action> recordChange;
        private Action repaint;

        // The result is rendered into the spare texture first, so a failed render keeps the last result on screen.
        private RenderTexture before, after, spare;
        private Texture2D diagnosticsBefore, diagnosticsAfter;
        // The inputs the previews were made from; null makes the next Layout event prepare them again.
        private string state;
        // The correspondence inputs prepared once already: only a first preparation can take long (progress bar).
        private string preparedInputs;
        private string outlineKey;
        private List<FBXUVIsland> islands;
        private Vector2[][] outlines;
        private float[] pinUvRadii;
        // Target-side nipple UVs of the shown correspondence (empty when the landmark was not used).
        private Vector2[] landmarkUvs = new Vector2[0];
        private List<string> sharedUvWarnings = new List<string>();

        private Color32[] diagnosticsPixels;

        private FBXUVBodyManualSettings draft;
        // Held from BeginDrag to the end of the drag only (ruling T12-4): every refresh cancels the drag first, so it never
        // outlives one, and the drag steps do not hash the meshes again. Another avatar pair prepared meanwhile can make the
        // cache drop it; HeldEntry() then gives null and the drag continues on the live entry.
        private FBXUVBodyCorrespondenceEntry dragEntry;
        private Vector2 grabOffset;
        private bool validDraft, moved;
        private int selected = -1, tool, dragControl;
        private bool showOutline = true, showPins = true, showDiagnostics, compare;

        internal string PendingReason { get; private set; }
        internal string PreviewError { get; private set; }
        internal bool Dragging { get; private set; }
        internal int SelectedPin => selected;
        internal Texture ResultTexture => after;
        internal Texture UncorrectedTexture => before;
        internal Texture2D DiagnosticsTexture => diagnosticsAfter;
        internal Texture2D UncorrectedDiagnosticsTexture => diagnosticsBefore;
        internal bool ShowDiagnostics { get => showDiagnostics; set => showDiagnostics = value; }
        internal int Tool { get => tool; set => tool = Mathf.Clamp(value, 0, toolLabels.Length - 1); }
        // The zoomed image at the last Repaint, relative to that event's mouse position (the layer window records the same
        // position in window coordinates; tests send real mouse events with the two).
        internal Rect ImageRectFromRepaintMouse { get; private set; }

        // The editing preview is at most 1024 px (spec §6.2); the build keeps min(canvas, 2048).
        internal static int PreviewResolution(FBXUVBodyOverlayTransferLayer owner)
            => owner == null ? 0 : Mathf.Min(owner.ResolveCanvasResolution(), MaxPreviewResolution);

        // A different layer cancels the drag of the previous one and drops its previews.
        internal void Bind(FBXUVBodyOverlayTransferLayer owner, Action<string, Action> record, Action onRepaint)
        {
            if (!ReferenceEquals(layer, owner))
            {
                Reset();
                layer = owner;
            }
            recordChange = record;
            repaint = onRepaint;
        }

        internal void Draw(FBXUVBodyOverlayTransferLayer layer, Rect area, Action<string, Action> recordChange, Action repaint)
        {
            Bind(layer, recordChange, repaint);
            if (this.layer == null) return;
            var e = Event.current;
            if (Dragging && e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape)
            {
                CancelDrag();
                e.Use();
                Changed();
            }
            if (e.type == EventType.Layout) EnsurePrepared();
            // Taken before the side panel, whose controls change with the selection, so the drag keeps its id.
            var control = GUIUtility.GetControlID(ControlHint, FocusType.Passive, area);
            var settings = draft ?? this.layer.manual ?? new FBXUVBodyManualSettings();
            DrawPanel(settings);
            // Last: clicks here change the panel, which is laid out already for this event.
            DrawViewport(area, control, settings);
        }

        // Undo/redo, project and reference changes: prepare the previews again on the next Layout event.
        internal void Invalidate()
        {
            AbortDrag();
            state = null;
        }

        // Saved values changed outside this editor (Inspector, scripts) since the previews were made.
        internal bool IsStale(FBXUVBodyOverlayTransferLayer owner)
            => state != null && owner != null && ReferenceEquals(owner, layer) && !Dragging && state != Key(owner);

        internal void BeginDrag(int pinIndex, Vector2 uv)
        {
            if (layer == null || Dragging) return;
            EnsurePrepared();
            var saved = layer.manual;
            if (saved?.pins == null || pinIndex < 0 || pinIndex >= saved.pins.Count || saved.pins[pinIndex] == null) return;
            selected = pinIndex;
            // Fixed pins hold their point; pins do nothing while the correction is off.
            if (saved.pins[pinIndex].fixedPoint || !saved.enabled) return;
            if (!layer.TryPrepareCorrespondence(PreviewResolution(layer), out var entry, out var reason))
            {
                PendingReason = reason;
                return;
            }
            dragEntry = entry;
            draft = saved.Copy();
            grabOffset = draft.pins[pinIndex].position - uv;
            validDraft = moved = false;
            PendingReason = null;
            Dragging = true;
        }

        // Previews the draft only; the layer is written on EndDrag.
        internal void DragTo(Vector2 uv)
        {
            if (!Dragging || selected < 0 || selected >= draft.pins.Count) return;
            if (HeldEntry() == null)
            {
                // The cache dropped the held entry: the draft goes on the live one, which the layer's renders share.
                if (!layer.TryPrepareCorrespondence(PreviewResolution(layer), out var live, out var prepareReason))
                {
                    CancelDrag();
                    PendingReason = prepareReason;
                    return;
                }
                dragEntry = live;
            }
            moved = true;
            draft.pins[selected].position = uv + grabOffset;
            validDraft = TryShow(draft, dragEntry, out var reason);
            PendingReason = validDraft ? null : reason;
        }

        // True when the move was saved (one Undo step). A rejected move keeps the saved pins and its reason.
        internal bool EndDrag()
        {
            if (!Dragging) return false;
            var result = validDraft ? draft : null;
            var reason = PendingReason;
            var draftShown = moved;
            var entry = HeldEntry();
            StopDrag();
            if (result != null)
            {
                Commit("補正ピンを移動", result);
                return true;
            }
            if (!draftShown) return false;
            // An earlier step of the drag may be on the shared map and on screen: show the saved pins again.
            ShowSaved(entry);
            if (PendingReason == null) PendingReason = reason;
            repaint?.Invoke();
            return false;
        }

        internal void CancelDrag()
        {
            if (!Dragging) return;
            var draftShown = moved;
            var entry = HeldEntry();
            StopDrag();
            PendingReason = null;
            if (draftShown) ShowSaved(entry);
            repaint?.Invoke();
        }

        // Places a pin at the clicked target UV. A correction pin gets the origin whose point the saved pins show there
        // (inverse placement, ruling T12-2), so adding it does not change the picture; a fixed pin keeps the base mapping
        // (origin = position). Off the body, or when the pins cannot be applied, nothing is added and PendingReason says why.
        internal bool AddPin(Vector2 uv, bool fixedPoint)
        {
            if (layer == null || Dragging) return false;
            if (!layer.TryPrepareCorrespondence(PreviewResolution(layer), out var entry, out var reason))
            {
                PendingReason = reason;
                return false;
            }
            var saved = Saved();
            var origin = fixedPoint ? uv : ShownOrigin(entry, saved, uv);
            var candidate = saved.Copy();
            // The first pin turns the correction on; with saved pins the switch stays where the user put it.
            if (candidate.pins.Count == 0) candidate.enabled = true;
            candidate.pins.Add(new FBXUVBodyPin { origin = origin, position = uv, fixedPoint = fixedPoint });
            if (!Change(fixedPoint ? "固定ピンを追加" : "補正ピンを追加", candidate, entry)) return false;
            selected = candidate.pins.Count - 1;
            tool = 0;
            return true;
        }

        // The target UV whose point the saved pins show at uv; uv itself when they move nothing there or cannot be applied.
        private static Vector2 ShownOrigin(FBXUVBodyCorrespondenceEntry entry, FBXUVBodyManualSettings saved, Vector2 uv)
        {
            if (saved == null || !saved.enabled || saved.pins == null || saved.pins.Count == 0) return uv;
            if (!FBXUVBodyPinWarp.TryCreate(entry.Correspondence, saved.pins, out var warp, out _)) return uv;
            return warp.TryInverseUv(uv, out var origin) ? origin : uv;
        }

        internal bool SetRadius(int pinIndex, float meters)
        {
            if (!TryCandidate(pinIndex, out var candidate)) return false;
            candidate.pins[pinIndex].radius = Mathf.Clamp(meters, MinRadiusCentimeters / 100f, MaxRadiusCentimeters / 100f);
            return Change("補正ピンの影響範囲を変更", candidate);
        }

        internal bool ResetMove(int pinIndex)
        {
            if (!TryCandidate(pinIndex, out var candidate)) return false;
            candidate.pins[pinIndex].position = candidate.pins[pinIndex].origin;
            return Change("補正ピンの移動を戻す", candidate);
        }

        internal bool DeletePin(int pinIndex)
        {
            if (!TryCandidate(pinIndex, out var candidate)) return false;
            candidate.pins.RemoveAt(pinIndex);
            if (!Change("補正ピンを削除", candidate)) return false;
            selected = -1;
            return true;
        }

        internal bool ResetAll()
        {
            if (layer == null || Dragging) return false;
            if (!Change(FBXUVManualCorrectionLabels.ResetAll, new FBXUVBodyManualSettings { enabled = Saved().enabled })) return false;
            selected = -1;
            return true;
        }

        // The button's entry point: nothing to reset or a drag in progress does nothing; otherwise asks before ResetAll().
        internal bool RequestResetAll()
        {
            if (layer == null || Dragging) return false;
            var count = Saved().pins?.Count ?? 0;
            return count > 0 && FBXUVResetConfirmation.Confirm(count) && ResetAll();
        }

        internal bool SetEnabled(bool enabled)
        {
            if (layer == null || Dragging) return false;
            var candidate = Saved().Copy();
            candidate.enabled = enabled;
            return Change(enabled ? FBXUVManualCorrectionLabels.Enable : "手補正を無効にする", candidate);
        }

        internal void Reset()
        {
            AbortDrag();
            ReleasePreviews();
            state = null;
            selected = -1;
            tool = 0;
            compare = false;
            PendingReason = null;
            PreviewError = null;
        }

        public void Dispose()
        {
            Reset();
            layer = null;
            preparedInputs = null;
        }

        private FBXUVBodyManualSettings Saved() => layer.manual ?? new FBXUVBodyManualSettings();

        private static bool CanAddPins(FBXUVBodyManualSettings settings)
            => settings.enabled || settings.pins == null || settings.pins.Count == 0;

        private bool TryCandidate(int pinIndex, out FBXUVBodyManualSettings candidate)
        {
            candidate = null;
            if (layer == null || Dragging) return false;
            candidate = Saved().Copy();
            return pinIndex >= 0 && pinIndex < candidate.pins.Count && candidate.pins[pinIndex] != null;
        }

        // Saves the candidate when it can be shown; otherwise nothing changes and PendingReason says why.
        private bool Change(string undoLabel, FBXUVBodyManualSettings candidate, FBXUVBodyCorrespondenceEntry entry = null)
        {
            if (!TryShow(candidate, entry, out var reason))
            {
                PendingReason = reason;
                return false;
            }
            PendingReason = null;
            Commit(undoLabel, candidate);
            return true;
        }

        // The preview and the shared map show these settings already. One recordChange = one Undo step.
        private void Commit(string undoLabel, FBXUVBodyManualSettings settings)
        {
            var saved = settings.Copy();
            recordChange(undoLabel, () => layer.manual = saved);
            state = Key(layer);
            repaint?.Invoke();
        }

        private void StopDrag()
        {
            if (dragControl != 0 && GUIUtility.hotControl == dragControl) GUIUtility.hotControl = 0;
            dragControl = 0;
            Dragging = false;
            draft = null;
            dragEntry = null;
            validDraft = moved = false;
        }

        // Ends a drag without showing anything (refresh, layer switch, dispose); the shared map gets the saved pins back.
        private void AbortDrag()
        {
            if (!Dragging) return;
            var entry = HeldEntry();
            StopDrag();
            // A dropped entry is shared by no one any more, and the draft was only there: nothing to restore.
            if (entry != null) RestoreSavedMap(entry);
        }

        // The entry the drag holds; null once the cache has dropped it, so the callers obtain the live one instead of
        // applying pins to, or rendering, a map no layer shares any more.
        private FBXUVBodyCorrespondenceEntry HeldEntry() => dragEntry != null && !dragEntry.IsDisposed ? dragEntry : null;

        // Prepares the previews when their inputs changed (the GUI calls it on Layout events).
        internal void EnsurePrepared()
        {
            if (layer == null || Dragging) return;
            var key = Key(layer);
            if (key == state) return;
            state = key;
            Prepare();
        }

        // Renders the uncorrected map and the saved pins. The shared map is left with the saved pins (the base map when they
        // cannot be applied, which PendingReason then reports).
        private void Prepare()
        {
            PreviewError = null;
            PendingReason = null;
            var resolution = PreviewResolution(layer);
            var inputs = CorrespondenceInputs(layer, resolution);
            var first = inputs != preparedInputs;
            try
            {
                if (first) EditorUtility.DisplayProgressBar("ボディ転送", "対応づけを計算しています…", .5f);
                if (!layer.TryPrepareCorrespondence(resolution, out var entry, out var reason))
                {
                    Fail(reason);
                    return;
                }
                preparedInputs = inputs;
                var map = entry.Correspondence;
                UpdateOutlines(entry);
                // 「押している間は手補正前」 shows Base*.
                FBXUVBodyOverlayTransferRenderer.UploadRegion(entry, FBXUVBodyPinWarp.Restore(map));
                Render(entry, ref before);
                UpdateDiagnostics(ref diagnosticsBefore, map, map.BaseValid);
                ShowSaved(entry);
                var count = layer.manual?.pins?.Count ?? 0;
                if (selected >= count) selected = -1;
            }
            catch (Exception exception)
            {
                Fail(exception.Message);
            }
            finally
            {
                if (first) EditorUtility.ClearProgressBar();
            }
        }

        // Shows the saved pins and leaves them on the shared map, as the layer's own TryPrepare does (the same
        // TryApplyPins), on the entry in hand (null: obtained here). When they cannot be applied (spec §5.2) the base map
        // is shown, as the uncorrected view does, and PendingReason says why.
        private void ShowSaved(FBXUVBodyCorrespondenceEntry entry)
        {
            if (layer == null) return;
            try
            {
                if (entry == null && !layer.TryPrepareCorrespondence(PreviewResolution(layer), out entry, out var prepareReason))
                {
                    Fail(prepareReason);
                    return;
                }
                var saved = Saved();
                if (FBXUVBodyOverlayTransferLayer.TryApplyPins(entry, saved, out var reason)) PendingReason = null;
                else
                {
                    PendingReason = reason;
                    FBXUVBodyOverlayTransferRenderer.UploadRegion(entry, FBXUVBodyPinWarp.Restore(entry.Correspondence));
                }
                ShowResult(entry, saved);
            }
            catch (Exception exception)
            {
                Fail(exception.Message);
            }
        }

        // Applies settings that are not saved yet (a drag draft or a candidate) to the shared map and renders them, on the
        // entry in hand (null: obtained here). On false the last result stays on screen and the shared map carries the
        // saved pins.
        private bool TryShow(FBXUVBodyManualSettings settings, FBXUVBodyCorrespondenceEntry entry, out string reason)
        {
            try
            {
                if (entry == null && !layer.TryPrepareCorrespondence(PreviewResolution(layer), out entry, out reason)) return false;
                // On false the map is unchanged.
                if (!FBXUVBodyOverlayTransferLayer.TryApplyPins(entry, settings, out reason)) return false;
                ShowResult(entry, settings);
                reason = null;
                return true;
            }
            catch (Exception exception)
            {
                reason = exception.Message;
                // The settings may be on the map already (the exception came after they were applied).
                RestoreSavedMap(entry);
                return false;
            }
        }

        // The shared map must not keep a draft: the saved pins, or the base map when they cannot be applied. Never renders,
        // so it is safe with an entry the cache has dropped meanwhile (its GPU half is gone and nothing is uploaded).
        private void RestoreSavedMap(FBXUVBodyCorrespondenceEntry entry)
        {
            if (layer == null) return;
            try
            {
                if (entry == null && !layer.TryPrepareCorrespondence(PreviewResolution(layer), out entry, out _)) return;
                if (FBXUVBodyOverlayTransferLayer.TryApplyPins(entry, Saved(), out _)) return;
                FBXUVBodyOverlayTransferRenderer.UploadRegion(entry, FBXUVBodyPinWarp.Restore(entry.Correspondence));
            }
            catch (Exception exception)
            {
                Debug.LogWarning("FBX UV Body Overlay Transfer: 補正ピンの状態を戻せませんでした。" + exception.Message, layer);
            }
        }

        private void ShowResult(FBXUVBodyCorrespondenceEntry entry, FBXUVBodyManualSettings settings)
        {
            var map = entry.Correspondence;
            landmarkUvs = LandmarkMarkerUvs(map.Landmarks);
            Render(entry, ref spare);
            var shown = spare;
            spare = after;
            after = shown;
            UpdateDiagnostics(ref diagnosticsAfter, map, map.Valid);
            pinUvRadii = UvRadii(map, settings);
            sharedUvWarnings = settings != null && settings.enabled
                ? FBXUVBodyPinWarp.SharedUvWarnings(map, settings.pins)
                : new List<string>();
        }

        // The texture is stored before rendering, so it is released even when rendering fails.
        private void Render(FBXUVBodyCorrespondenceEntry entry, ref RenderTexture texture)
        {
            var size = entry.Correspondence.Resolution;
            if (texture != null && (texture.width != size || texture.height != size)) Destroy(ref texture);
            if (texture == null)
            {
                texture = new RenderTexture(size, size, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear)
                    { enableRandomWrite = true, hideFlags = HideFlags.HideAndDontSave, name = "ボディ転送プレビュー" };
                texture.Create();
            }
            FBXUVBodyOverlayTransferRenderer.Render(layer, texture, entry);
        }

        // Magenta: no source found. Yellow: left/right shared UV (one side only is used).
        private void UpdateDiagnostics(ref Texture2D texture, FBXUVBodyCorrespondence map, bool[] valid)
        {
            if (!showDiagnostics)
            {
                Destroy(ref texture);
                return;
            }
            var size = map.Resolution;
            if (texture != null && texture.width != size) Destroy(ref texture);
            if (texture == null)
            {
                texture = new Texture2D(size, size, TextureFormat.RGBA32, false, true)
                {
                    hideFlags = HideFlags.HideAndDontSave,
                    filterMode = FilterMode.Point,
                    wrapMode = TextureWrapMode.Clamp,
                    name = "ボディ転送の診断表示",
                };
            }
            // One buffer for both overlays and every drag step.
            if (diagnosticsPixels == null || diagnosticsPixels.Length != size * size) diagnosticsPixels = new Color32[size * size];
            else Array.Clear(diagnosticsPixels, 0, diagnosticsPixels.Length);
            var pixels = diagnosticsPixels;
            for (var i = 0; i < map.Texels.Length; i++)
            {
                if (!valid[i]) pixels[map.Texels[i]] = invalidColor;
                else if (map.TargetAmbiguous != null && map.TargetAmbiguous[i]) pixels[map.Texels[i]] = sharedColor;
            }
            texture.SetPixels32(pixels);
            texture.Apply(false, false);
        }

        // Island outlines of the target body in UV: the triangle edges that only one triangle of the island uses.
        private void UpdateOutlines(FBXUVBodyCorrespondenceEntry entry)
        {
            if (outlines != null && outlineKey == entry.Key) return;
            var target = entry.Correspondence.Target;
            var result = new List<FBXUVIsland>();
            foreach (var part in target.Parts)
            {
                var triangles = new List<FBXUVTriangle>(part.TriangleCount);
                for (var local = 0; local < part.TriangleCount; local++)
                {
                    var t = part.FirstTriangle + local;
                    triangles.Add(new FBXUVTriangle(t, target.Uvs[target.Triangles[t * 3]],
                        target.Uvs[target.Triangles[t * 3 + 1]], target.Uvs[target.Triangles[t * 3 + 2]]));
                }
                foreach (var island in FBXUVIslandExtractor.Extract(triangles)) result.Add(new FBXUVIsland(result.Count, island.triangles));
            }
            islands = result;
            outlines = new Vector2[result.Count][];
            for (var i = 0; i < result.Count; i++) outlines[i] = Boundary(result[i]);
            outlineKey = entry.Key;
        }

        // Pairs of segment ends.
        private static Vector2[] Boundary(FBXUVIsland island)
        {
            var edges = new Dictionary<(long, long, long, long), (Vector2 A, Vector2 B, int Count)>();
            foreach (var triangle in island.triangles)
            {
                for (var corner = 0; corner < 3; corner++)
                {
                    var a = triangle.GetPoint(corner);
                    var b = triangle.GetPoint((corner + 1) % 3);
                    var ka = Quantize(a);
                    var kb = Quantize(b);
                    if (ka == kb) continue;
                    var key = ka.CompareTo(kb) < 0 ? (ka.Item1, ka.Item2, kb.Item1, kb.Item2) : (kb.Item1, kb.Item2, ka.Item1, ka.Item2);
                    edges[key] = edges.TryGetValue(key, out var edge) ? (edge.A, edge.B, edge.Count + 1) : (a, b, 1);
                }
            }
            var result = new List<Vector2>();
            foreach (var edge in edges.Values)
            {
                if (edge.Count != 1) continue;
                result.Add(edge.A);
                result.Add(edge.B);
            }
            return result.ToArray();
        }

        private static (long, long) Quantize(Vector2 uv)
            => ((long)Math.Round(uv.x / FBXUVIslandExtractor.EdgeQuantizationEpsilon), (long)Math.Round(uv.y / FBXUVIslandExtractor.EdgeQuantizationEpsilon));

        // Each pin's radius in UV near its origin, for drawing: the radius is in meters (3D), the origin triangle's UV → 3D
        // scale converts it. 0 where the origin is off the body.
        private static float[] UvRadii(FBXUVBodyCorrespondence map, FBXUVBodyManualSettings settings)
        {
            var pins = settings?.pins;
            if (pins == null) return Array.Empty<float>();
            var result = new float[pins.Count];
            var resolution = map.Resolution;
            var target = map.Target;
            for (var k = 0; k < pins.Count; k++)
            {
                var pin = pins[k];
                if (pin == null || float.IsNaN(pin.origin.x) || float.IsNaN(pin.origin.y)) continue;
                int x = Mathf.FloorToInt(pin.origin.x * resolution), y = Mathf.FloorToInt(pin.origin.y * resolution);
                if (x < 0 || y < 0 || x >= resolution || y >= resolution) continue;
                var index = map.IndexOfTexel(y * resolution + x);
                if (index < 0) continue;
                var t = map.TargetTriangle[index];
                int i0 = target.Triangles[t * 3], i1 = target.Triangles[t * 3 + 1], i2 = target.Triangles[t * 3 + 2];
                Vector2 d1 = target.Uvs[i1] - target.Uvs[i0], d2 = target.Uvs[i2] - target.Uvs[i0];
                Vector3 e1 = target.Positions[i1] - target.Positions[i0], e2 = target.Positions[i2] - target.Positions[i0];
                var det = d1.x * d2.y - d2.x * d1.y;
                if (Mathf.Abs(det) < 1e-12f) continue;
                var du = (e1 * d2.y - e2 * d1.y) / det;
                var dv = (e2 * d1.x - e1 * d2.x) / det;
                var metersPerUv = (du.magnitude + dv.magnitude) * .5f;
                if (metersPerUv > 0f && !float.IsInfinity(metersPerUv)) result[k] = pin.radius / metersPerUv;
            }
            return result;
        }

        private void Fail(string reason)
        {
            PreviewError = string.IsNullOrEmpty(reason) ? "プレビューを作成できません。" : reason;
            ReleasePreviews();
        }

        private void ReleasePreviews()
        {
            Destroy(ref before);
            Destroy(ref after);
            Destroy(ref spare);
            Destroy(ref diagnosticsBefore);
            Destroy(ref diagnosticsAfter);
            diagnosticsPixels = null;
            islands = null;
            outlines = null;
            outlineKey = null;
            pinUvRadii = null;
            landmarkUvs = new Vector2[0];
            sharedUvWarnings = new List<string>();
        }

        private string Key(FBXUVBodyOverlayTransferLayer owner)
        {
            var texture = FBXUVCanvasTargetUtility.ResolveTargetTexture(owner);
            return EditorJsonUtility.ToJson(owner) + "|" + Id(texture) + "|" + PreviewResolution(owner) + "|" + showDiagnostics;
        }

        internal static string CorrespondenceInputs(FBXUVBodyOverlayTransferLayer owner, int resolution)
            => Id(owner.sourceModelOrPrefab) + "|" + Id(owner.sourceBodyTexture) + "|" + Id(owner.targetModelOrPrefab) + "|"
               + Id(FBXUVCanvasTargetUtility.ResolveTargetTexture(owner)) + "|" + resolution + "|" + owner.alignNipples
               + "|" + ShapeInput(owner.sourceModelOrPrefab, owner.sourceBodyTexture, owner.sourceNippleShape)
               + "|" + ShapeInput(owner.targetModelOrPrefab, FBXUVCanvasTargetUtility.ResolveTargetTexture(owner), owner.targetNippleShape);

        // The selection and what it resolves to now: renaming or adding a shape key changes the correspondence.
        private static string ShapeInput(GameObject model, Texture texture, FBXUVBodyNippleShapeSelection selection)
        {
            var mode = selection == null ? FBXUVBodyNippleShapeMode.Auto : selection.mode;
            var name = selection == null ? "" : selection.shapeName;
            var resolved = model == null || texture == null ? null : FBXUVBodyNippleShape.Resolve(model, texture, selection, out _);
            return (int)mode + ":" + name + ":" + resolved;
        }

        private static int Id(Object value) => value == null ? 0 : value.GetInstanceID();

        // ---- GUI ----

        // Layout-changing actions end the GUI pass: the rest of it was laid out for the previous state.
        private void Changed()
        {
            repaint?.Invoke();
            GUIUtility.ExitGUI();
        }

        private void DrawPanel(FBXUVBodyManualSettings settings)
        {
            if (PreviewError != null) EditorGUILayout.HelpBox("プレビューを表示できません：" + PreviewError, MessageType.Warning);
            using (new EditorGUI.DisabledScope(Dragging))
            {
                var enabled = EditorGUILayout.ToggleLeft(FBXUVManualCorrectionLabels.Enable, settings.enabled);
                if (enabled != settings.enabled)
                {
                    SetEnabled(enabled);
                    Changed();
                }
                // Without pins the tools stay usable: the first pin turns the correction on (AddPin).
                var locked = !CanAddPins(settings);
                if (locked) tool = 0;
                using (new EditorGUI.DisabledScope(locked)) tool = GUILayout.Toolbar(tool, toolLabels);
                if (locked) EditorGUILayout.HelpBox(FBXUVManualCorrectionLabels.PinsLockedMessage, MessageType.Info);
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                showOutline = GUILayout.Toggle(showOutline, FBXUVManualCorrectionLabels.Outline);
                showPins = GUILayout.Toggle(showPins, "ピン");
                var diagnostics = GUILayout.Toggle(showDiagnostics, "診断表示");
                if (diagnostics != showDiagnostics)
                {
                    // The overlays are built on the next preparation (the state key includes the toggle).
                    showDiagnostics = diagnostics;
                    Changed();
                }
            }
            compare = GUILayout.RepeatButton(FBXUVManualCorrectionLabels.Compare);
            if (viewport.DrawControls(SelectedIsland(settings))) repaint?.Invoke();
            if (showDiagnostics)
                EditorGUILayout.HelpBox("マゼンタ：転送元が見つからないテクセル\n黄色：左右で共有されたUV（片側だけが使われます）", MessageType.None);

            EditorGUILayout.Space(4f);
            DrawSelected(settings);
            if (PendingReason != null) EditorGUILayout.HelpBox("未反映：" + PendingReason, MessageType.Warning);
            foreach (var warning in sharedUvWarnings) EditorGUILayout.HelpBox(warning, MessageType.Warning);
            using (new EditorGUI.DisabledScope(Dragging || settings.pins == null || settings.pins.Count == 0))
            {
                if (GUILayout.Button(FBXUVManualCorrectionLabels.ResetAll))
                {
                    RequestResetAll();
                    Changed();
                }
            }
            EditorGUILayout.HelpBox(
                "「補正ピンを追加」「固定ピンを追加」を押してから画像をクリックしてピンを置きます。最初のピンを置くと手補正が有効になります。補正ピンはドラッグで動かし、離すと確定します（Undo 1回で戻ります）。Escでドラッグを取り消します。固定ピンは手補正前の対応を保ちます。スクロールで拡大、中ボタンで移動。\n"
                + "影響範囲は3Dの距離なので、島の境目をまたいで両側が同じように動きます。\n"
                + "表示はこのレイヤー単体です（最大" + MaxPreviewResolution + "px）。他レイヤー・Shaderを含む仕上がりはTexTransTool のプレビューで確認してください。",
                MessageType.None);
        }

        private void DrawSelected(FBXUVBodyManualSettings settings)
        {
            var pins = settings.pins;
            if (pins == null || selected < 0 || selected >= pins.Count || pins[selected] == null)
            {
                EditorGUILayout.LabelField("ピンを選ぶと、影響範囲の変更・移動を戻す・削除ができます。", EditorStyles.wordWrappedMiniLabel);
                return;
            }
            var pin = pins[selected];
            EditorGUILayout.LabelField($"選択中：{(pin.fixedPoint ? "固定" : "補正")}ピン {selected + 1}", EditorStyles.boldLabel);
            using (new EditorGUI.DisabledScope(Dragging || !settings.enabled))
            {
                EditorGUI.BeginChangeCheck();
                var centimeters = EditorGUILayout.Slider(
                    new GUIContent("影響範囲（cm）", "3Dの距離です。身長に関係なく、この距離の範囲が動きます。"),
                    pin.radius * 100f, MinRadiusCentimeters, MaxRadiusCentimeters);
                if (EditorGUI.EndChangeCheck())
                {
                    SetRadius(selected, centimeters / 100f);
                    Changed();
                }
                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUI.DisabledScope(pin.fixedPoint || pin.position == pin.origin))
                    {
                        if (GUILayout.Button(FBXUVManualCorrectionLabels.ResetMove))
                        {
                            ResetMove(selected);
                            Changed();
                        }
                    }
                    if (GUILayout.Button(FBXUVManualCorrectionLabels.DeletePin))
                    {
                        DeletePin(selected);
                        Changed();
                    }
                }
            }
        }

        private FBXUVIsland SelectedIsland(FBXUVBodyManualSettings settings)
        {
            var pins = settings.pins;
            if (islands == null || pins == null || selected < 0 || selected >= pins.Count || pins[selected] == null) return null;
            return FBXUVIslandExtractor.HitTest(islands, pins[selected].origin);
        }

        private void DrawViewport(Rect area, int control, FBXUVBodyManualSettings settings)
        {
            GUI.BeginGroup(area);
            var local = new Rect(Vector2.zero, area.size);
            if (viewport.HandleInput(local)) repaint?.Invoke();
            var image = viewport.ImageRect(local);
            if (Event.current.type == EventType.Repaint)
            {
                ImageRectFromRepaintMouse = new Rect(image.position - Event.current.mousePosition, image.size);
                Paint(local, image, settings);
            }
            HandleInput(local, image, control, settings);
            GUI.EndGroup();
        }

        private void Paint(Rect local, Rect image, FBXUVBodyManualSettings settings)
        {
            EditorGUI.DrawRect(local, backgroundColor);
            var target = FBXUVCanvasTargetUtility.ResolveTargetTexture(layer);
            if (target != null) EditorGUI.DrawPreviewTexture(image, target, null, ScaleMode.StretchToFill);
            var result = compare ? before : after;
            if (result != null) GUI.DrawTexture(image, result, ScaleMode.StretchToFill, true);
            var diagnostics = compare ? diagnosticsBefore : diagnosticsAfter;
            if (showDiagnostics && diagnostics != null) GUI.DrawTexture(image, diagnostics, ScaleMode.StretchToFill, true);
            Handles.BeginGUI();
            var old = Handles.color;
            if (showOutline) DrawOutlines(image, SelectedIsland(settings));
            if (showPins) DrawPins(image, settings);
            if (showPins) DrawLandmarks(image);
            Handles.color = old;
            Handles.EndGUI();
        }

        private void DrawOutlines(Rect image, FBXUVIsland highlighted)
        {
            if (outlines == null) return;
            var count = 0;
            for (var i = 0; i < outlines.Length; i++)
            {
                if (highlighted == null || i != highlighted.id) count += outlines[i].Length;
            }
            var lines = new Vector3[count];
            var cursor = 0;
            for (var i = 0; i < outlines.Length; i++)
            {
                if (highlighted != null && i == highlighted.id) continue;
                foreach (var uv in outlines[i]) lines[cursor++] = FBXUVPreviewViewport.ToGui(image, uv);
            }
            Handles.color = outlineColor;
            if (lines.Length > 0) Handles.DrawLines(lines);
            if (highlighted == null || highlighted.id >= outlines.Length) return;
            var selectedLines = outlines[highlighted.id];
            var points = new Vector3[selectedLines.Length];
            for (var i = 0; i < points.Length; i++) points[i] = FBXUVPreviewViewport.ToGui(image, selectedLines[i]);
            Handles.color = highlightColor;
            if (points.Length > 0) Handles.DrawLines(points);
        }

        // Hollow cyan ring with a dark outline at each target nipple the landmark alignment used (spec §4.5).
        private void DrawLandmarks(Rect image)
        {
            if (landmarkUvs == null) return;
            foreach (var uv in landmarkUvs)
            {
                var point = FBXUVPreviewViewport.ToGui(image, uv);
                Handles.color = new Color(0f, 0f, 0f, .8f);
                Handles.DrawWireDisc(point, Vector3.forward, 7f, 3f);
                Handles.color = new Color(0f, .9f, 1f);
                Handles.DrawWireDisc(point, Vector3.forward, 6f, 2f);
            }
        }

        // The target nipple UVs, left then right, without duplicates. Empty unless the landmark alignment was used.
        internal static Vector2[] LandmarkMarkerUvs(FBXUVBodyLandmarkResult result)
        {
            var uvs = new List<Vector2>();
            if (result == null || !result.Used) return uvs.ToArray();
            foreach (var side in new[] { result.Left, result.Right })
            {
                if (side?.TargetUvs == null) continue;
                foreach (var uv in side.TargetUvs)
                {
                    if (!uvs.Contains(uv)) uvs.Add(uv);
                }
            }
            return uvs.ToArray();
        }

        private void DrawPins(Rect image, FBXUVBodyManualSettings settings)
        {
            var pins = settings.pins;
            if (pins == null) return;
            for (var i = 0; i < pins.Count; i++)
            {
                var pin = pins[i];
                if (pin == null) continue;
                var point = FBXUVPreviewViewport.ToGui(image, compare ? pin.origin : pin.position);
                var color = i == selected ? (PendingReason != null ? Color.red : Color.yellow) : Color.white;
                if (!compare && !pin.fixedPoint && pin.position != pin.origin)
                {
                    Handles.color = new Color(color.r, color.g, color.b, .8f);
                    Handles.DrawDottedLine(FBXUVPreviewViewport.ToGui(image, pin.origin), point, 3);
                }
                Handles.color = color;
                if (pin.fixedPoint) EditorGUI.DrawRect(new Rect(point.x - 4, point.y - 4, 8, 8), color);
                else Handles.DrawSolidDisc(point, Vector3.forward, 4);
                GUI.Label(new Rect(point.x + 6, point.y - 10, 40, 20), (i + 1).ToString());
                if (i == selected && pinUvRadii != null && i < pinUvRadii.Length && pinUvRadii[i] > 0f)
                    Handles.DrawWireDisc(point, Vector3.forward, pinUvRadii[i] * image.width);
            }
        }

        private void HandleInput(Rect local, Rect image, int control, FBXUVBodyManualSettings settings)
        {
            var e = Event.current;
            if (Dragging && dragControl == control)
            {
                if (e.type == EventType.MouseUp)
                {
                    EndDrag();
                    e.Use();
                    repaint?.Invoke();
                    return;
                }
                if (e.type == EventType.MouseDrag)
                {
                    DragTo(FBXUVPreviewViewport.ToUv(image, e.mousePosition));
                    e.Use();
                    repaint?.Invoke();
                    return;
                }
            }
            if (e.type != EventType.MouseDown || e.button != 0 || Dragging || !local.Contains(e.mousePosition)) return;
            var uv = FBXUVPreviewViewport.ToUv(image, e.mousePosition);
            if (tool != 0 && CanAddPins(settings)) AddPin(uv, tool == 2);
            else
            {
                selected = HitTest(image, settings, e.mousePosition);
                if (selected >= 0)
                {
                    BeginDrag(selected, uv);
                    if (Dragging)
                    {
                        dragControl = control;
                        GUIUtility.hotControl = control;
                    }
                }
            }
            e.Use();
            repaint?.Invoke();
        }

        // The topmost (last drawn) pin within PinHitPixels of the point, or -1.
        private int HitTest(Rect image, FBXUVBodyManualSettings settings, Vector2 point)
        {
            var pins = settings.pins;
            if (pins == null || !showPins) return -1;
            for (var i = pins.Count - 1; i >= 0; i--)
            {
                if (pins[i] == null) continue;
                var shown = FBXUVPreviewViewport.ToGui(image, pins[i].position);
                if (Vector2.Distance(shown, point) < PinHitPixels) return i;
            }
            return -1;
        }

        private static void Destroy(ref RenderTexture texture)
        {
            if (texture != null)
            {
                texture.Release();
                Object.DestroyImmediate(texture);
            }
            texture = null;
        }

        private static void Destroy(ref Texture2D texture)
        {
            if (texture != null) Object.DestroyImmediate(texture);
            texture = null;
        }
    }
}
