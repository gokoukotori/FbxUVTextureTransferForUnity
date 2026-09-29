using System;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace GokouKotori.FBXUVTextureTransfer.Editor
{
    internal sealed class FBXUVEyeManualEditor : IDisposable
    {
        private string state, error;
        private FBXUVEyeMapping baselineMap;
        private FBXUVEyeManualWarp coordinates, previewWarp;
        private FBXUVEyeManualSettings draft;
        private RenderTexture before, after;
        private int selected = -1, tool, dragControl;
        private bool validDraft, showPins = true, showOutline = true, showGrid, compare;
        private readonly Vector2[] centers = { Vector2.one * .5f, Vector2.one * .5f };
        private readonly float[] zoom = { 1, 1 };
        private FBXUVEyeTextureTransferLayer layer;
        private FBXUVEyeBinding eye;
        private Action changed, repaint;

        internal void Draw(FBXUVEyeTextureTransferLayer owner, FBXUVEyeBinding binding, float width, Action onChanged, Action onRepaint)
        {
            layer = owner; eye = binding; changed = onChanged; repaint = onRepaint;
            var key = EditorJsonUtility.ToJson(layer);
            if (state != key) { Reset(); state = key; Prepare(); }
            var settings = draft ?? eye.manual ?? new FBXUVEyeManualSettings();
            using (new EditorGUI.DisabledScope(dragControl != 0))
            {
                var enabled = EditorGUILayout.ToggleLeft("手補正を有効にする", settings.enabled);
                if (enabled != settings.enabled)
                {
                    var candidate = settings.Copy(); candidate.enabled = enabled;
                    var rendered = TryPreview(candidate);
                    if (!enabled || rendered) Commit(candidate);
                }
                tool = GUILayout.Toolbar(tool, new[] { "移動", "補正ピンを追加", "固定ピンを追加" });
            }
            EditorGUILayout.HelpBox(eye.pupilMode == FBXUVEyePupilMode.Separate
                ? "虹彩を補正中。目の外周と別パーツの瞳孔は固定です。瞳孔は下の「自動配置からの調整」で変更します。"
                : "画像一体型：瞳孔・ハイライトを含む絵柄を補正します。目の外周は固定です。", MessageType.Info);
            if (baselineMap != null)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    DrawImage(0, "転送元・虹彩", layer.sourceTexture, width);
                    DrawImage(1, compare ? "Eyeレイヤー・手補正前" : "Eyeレイヤー・補正結果", compare ? before : after, width);
                }
                using (new EditorGUILayout.HorizontalScope())
                {
                    showOutline = GUILayout.Toggle(showOutline, "輪郭"); showPins = GUILayout.Toggle(showPins, "ピン");
                    showGrid = GUILayout.Toggle(showGrid, "変形の格子");
                    compare = GUILayout.RepeatButton("押している間は手補正前");
                    if (GUILayout.Button("目を拡大")) FrameEyes();
                }
                DrawSelected(settings);
            }
            if (!string.IsNullOrEmpty(error)) EditorGUILayout.HelpBox("未反映：" + error, MessageType.Warning);
            using (new EditorGUI.DisabledScope(dragControl != 0))
                if (GUILayout.Button("手補正をすべてリセット"))
                {
                    var candidate = new FBXUVEyeManualSettings { enabled = settings.enabled };
                    candidate.BindRegions(eye); TryPreview(candidate);
                    selected = -1; Commit(candidate);
                }
            EditorGUILayout.HelpBox("結果画像でクリックしてピンを追加し、ドラッグで移動します。元画像側では補正ピンの参照位置を調整できます。固定ピンは現在の一点を保持します。スクロールで拡大、中ボタンで移動。Escでドラッグを取り消します。\n表示は選択したEyeレイヤー単体です。他レイヤー・Shaderを含む仕上がりはTTTプレビューで確認してください。", MessageType.None);
        }

        private void Prepare()
        {
            try
            {
                baselineMap = Map(new FBXUVEyeManualSettings());
                coordinates = new FBXUVEyeManualWarp(baselineMap);
                before = Render(baselineMap);
                FrameEyes();
                if (!TryPreview(eye.manual ?? new FBXUVEyeManualSettings())) after = Render(baselineMap);
            }
            catch (Exception e) { error = e.Message; }
        }

        private FBXUVEyeMapping Map(FBXUVEyeManualSettings settings)
        {
            var binding = new FBXUVEyeBinding
            {
                name = eye.name, enabled = eye.enabled, transferMethod = eye.transferMethod, pupilMode = eye.pupilMode,
                iris = eye.iris, pupil = eye.pupil, target = eye.target,
                pupilOffset = eye.pupilOffset, pupilScale = eye.pupilScale, pupilAspect = eye.pupilAspect, manual = settings
            };
            return FBXUVEyeMapping.Create(layer.sourceModelOrPrefab, layer.targetModelOrPrefab, binding, new FBXUVMeshAnalysisCache());
        }

        private RenderTexture Render(FBXUVEyeMapping mapping)
        {
            var texture = FBXUVCanvasHierarchyUtility.TryFindCanvas(layer, out var canvas, out _, out _)
                ? canvas.TargetTexture?.SelectTexture : null;
            var scale = texture == null ? 1 : Mathf.Min(1, 1024f / Mathf.Max(texture.width, texture.height));
            var rt = new RenderTexture(texture == null ? 512 : Mathf.Max(1, Mathf.RoundToInt(texture.width * scale)),
                texture == null ? 512 : Mathf.Max(1, Mathf.RoundToInt(texture.height * scale)), 0,
                RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear)
                { enableRandomWrite = true, hideFlags = HideFlags.HideAndDontSave, name = "Eye手補正プレビュー" };
            try
            {
                if (layer.sourceTexture == null || layer.ResolveEyeShader() == null || layer.ResolvePixelProcessShader() == null
                    || !SystemInfo.supportsComputeShaders || !layer.ResolveEyeShader().isSupported)
                    throw new InvalidOperationException("転送元テクスチャまたは描画環境を確認してください。");
                rt.Create(); FBXUVTextureTransferRenderer.Clear(rt);
                FBXUVEyeTextureTransferRenderer.Render(layer, rt, new[] { mapping });
                return rt;
            }
            catch { Destroy(rt); throw; }
        }

        private bool TryPreview(FBXUVEyeManualSettings candidate)
        {
            try
            {
                var mapping = Map(candidate);
                var result = Render(mapping);
                Destroy(after); after = result; previewWarp = mapping.ManualWarp; error = null;
                return true;
            }
            catch (Exception e) { error = e.Message; return false; }
        }

        internal static void SaveSettings(FBXUVEyeTextureTransferLayer owner, FBXUVEyeBinding binding, FBXUVEyeManualSettings settings)
        {
            Undo.RecordObject(owner, "Eye手補正を変更");
            binding.manual = settings.Copy();
            EditorUtility.SetDirty(owner); PrefabUtility.RecordPrefabInstancePropertyModifications(owner);
        }

        private void Commit(FBXUVEyeManualSettings settings)
        {
            SaveSettings(layer, eye, settings);
            draft = null; state = EditorJsonUtility.ToJson(layer); changed(); repaint();
        }

        private void DrawSelected(FBXUVEyeManualSettings settings)
        {
            if (settings.pins == null || selected < 0 || selected >= settings.pins.Count) return;
            var pin = settings.pins[selected]; if (pin == null) return;
            EditorGUILayout.LabelField($"選択中：{(pin.fixedPoint ? "固定" : "補正")}ピン {selected + 1}", EditorStyles.boldLabel);
            using (new EditorGUI.DisabledScope(dragControl != 0 || !settings.enabled))
            {
                EditorGUI.BeginChangeCheck();
                var radius = EditorGUILayout.Slider(new GUIContent("影響範囲", "目の幅・高さを1とした半径。外周・瞳孔の手前までに制限されます。"), pin.radius * .5f, .005f, 1);
                var offset = (pin.position - pin.origin) * .5f;
                using (new EditorGUI.DisabledScope(pin.fixedPoint)) offset = EditorGUILayout.Vector2Field("移動量（目の幅・高さ比）", offset);
                if (EditorGUI.EndChangeCheck())
                {
                    var candidate = settings.Copy(); candidate.pins[selected].radius = radius * 2;
                    if (!pin.fixedPoint) candidate.pins[selected].position = pin.origin + offset * 2;
                    if (TryPreview(candidate)) Commit(candidate);
                }
                EditorGUILayout.LabelField("実際の影響範囲", (coordinates.EffectiveRadius(pin.position, pin.radius) * .5f).ToString("F3"));
            }
            using (new EditorGUI.DisabledScope(dragControl != 0))
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(pin.fixedPoint))
                    if (GUILayout.Button("このピンの移動を戻す"))
                    {
                        var candidate = settings.Copy(); candidate.pins[selected].position = pin.origin;
                        if (TryPreview(candidate)) Commit(candidate);
                    }
                if (GUILayout.Button("ピンを削除"))
                {
                    var candidate = settings.Copy(); candidate.pins.RemoveAt(selected);
                    if (TryPreview(candidate) || candidate.pins.Count == 0) { selected = -1; Commit(candidate); }
                }
            }
        }

        private void DrawImage(int index, string label, Texture texture, float width)
        {
            using (new EditorGUILayout.VerticalScope(GUILayout.Width(width)))
            {
                EditorGUILayout.LabelField(label, EditorStyles.boldLabel);
                var rect = GUILayoutUtility.GetRect(width, Mathf.Min(width, 440), GUILayout.ExpandWidth(false));
                var control = GUIUtility.GetControlID(72011 + index, FocusType.Passive, rect);
                GUI.BeginGroup(rect);
                var local = new Rect(Vector2.zero, rect.size);
                EditorGUI.DrawRect(local, new Color(.16f, .16f, .16f));
                var size = Mathf.Min(rect.width, rect.height) * zoom[index];
                var image = new Rect(rect.width * .5f - centers[index].x * size,
                    rect.height * .5f - (1 - centers[index].y) * size, size, size);
                if (texture != null) GUI.DrawTexture(image, texture, ScaleMode.StretchToFill, true);
                if (Event.current.type == EventType.Repaint)
                {
                    Handles.BeginGUI(); var old = Handles.color;
                    if (showOutline)
                    {
                        Handles.color = Color.cyan;
                        var geometry = index == 0 ? baselineMap.Iris : baselineMap.Target;
                        foreach (var loop in geometry.Loops)
                        {
                            var points = loop.Select(p => geometry.Uvs[Array.IndexOf(geometry.Positions, p)]).ToArray();
                            for (var i = 0; i < points.Length; i++) Handles.DrawLine(ToGui(image, points[i]), ToGui(image, points[(i + 1) % points.Length]));
                        }
                    }
                    if (index == 1 && showGrid) DrawGrid(image);
                    if (showPins) DrawPins(index, image);
                    Handles.color = old; Handles.EndGUI();
                }
                HandleInput(index, local, image, control);
                GUI.EndGroup();
            }
        }

        private void DrawPins(int index, Rect image)
        {
            var settings = draft ?? eye.manual;
            if (settings?.pins == null) return;
            for (var i = 0; i < settings.pins.Count; i++)
            {
                var pin = settings.pins[i]; if (pin == null || !PinUv(index, pin, out var uv)) continue;
                var point = ToGui(image, uv);
                var color = i == selected && !string.IsNullOrEmpty(error) ? Color.red : i == selected ? Color.yellow : Color.white;
                Handles.color = color;
                if (pin.fixedPoint) EditorGUI.DrawRect(new Rect(point.x - 4, point.y - 4, 8, 8), color);
                else Handles.DrawSolidDisc(point, Vector3.forward, 4);
                GUI.Label(new Rect(point.x + 6, point.y - 10, 40, 20), (i + 1).ToString());
                if (index == 1 && i == selected)
                {
                    if (coordinates.TryTargetUv(pin.origin, out var origin)) Handles.DrawDottedLine(ToGui(image, origin), point, 3);
                    var radius = coordinates.EffectiveRadius(pin.position, pin.radius);
                    Vector3? last = null;
                    for (var k = 0; k <= 64; k++)
                    {
                        var a = k * Mathf.PI * 2 / 64;
                        if (!coordinates.TryTargetUv(pin.position + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * radius, out var edge)) { last = null; continue; }
                        var next = ToGui(image, edge); if (last.HasValue) Handles.DrawLine(last.Value, next); last = next;
                    }
                }
            }
        }

        private void DrawGrid(Rect image)
        {
            Handles.color = new Color(1, 1, 1, .35f);
            for (var axis = 0; axis < 2; axis++)
                for (var row = -8; row <= 8; row++)
                {
                    Vector3? last = null;
                    for (var col = -32; col <= 32; col++)
                    {
                        var p = axis == 0 ? new Vector2(row / 8f, col / 32f) : new Vector2(col / 32f, row / 8f);
                        if (!compare && previewWarp != null) p = previewWarp.Forward(p);
                        if (!coordinates.TryTargetUv(p, out var uv)) { last = null; continue; }
                        var next = ToGui(image, uv); if (last.HasValue) Handles.DrawLine(last.Value, next); last = next;
                    }
                }
        }

        private bool PinUv(int index, FBXUVEyeManualPin pin, out Vector2 uv) => index == 0
            ? coordinates.TrySourceUv(pin.origin, out uv) : coordinates.TryTargetUv(compare ? pin.origin : pin.position, out uv);

        private void HandleInput(int index, Rect rect, Rect image, int control)
        {
            var e = Event.current;
            if (dragControl != 0 && e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape)
            { CancelDrag(); e.Use(); repaint(); return; }
            if (e.type == EventType.MouseUp && dragControl == control)
            {
                GUIUtility.hotControl = 0; dragControl = 0;
                if (validDraft) Commit(draft); else { draft = null; TryPreview(eye.manual ?? new FBXUVEyeManualSettings()); }
                e.Use(); repaint(); return;
            }
            if (e.type == EventType.MouseDrag && dragControl == control)
            {
                var uv = ToUv(image, e.mousePosition);
                Vector2 point;
                var located = index == 0 ? coordinates.TryPointFromSourceUv(uv, out point) : coordinates.TryTargetPoint(uv, out point);
                if (!located) { validDraft = false; error = "目の領域外には移動できません。"; }
                else
                {
                    if (index == 0) draft.pins[selected].origin = point; else draft.pins[selected].position = point;
                    validDraft = TryPreview(draft);
                }
                e.Use(); repaint(); return;
            }
            if (!rect.Contains(e.mousePosition)) return;
            if (e.type == EventType.ScrollWheel && dragControl == 0)
            { zoom[index] = Mathf.Clamp(zoom[index] * Mathf.Exp(-e.delta.y * .12f), 1, 64); e.Use(); repaint(); }
            else if (e.type == EventType.MouseDrag && e.button == 2 && dragControl == 0)
            { centers[index] += new Vector2(-e.delta.x, e.delta.y) / image.width; e.Use(); repaint(); }
            else if (e.type == EventType.MouseDown && e.button == 0 && dragControl == 0 && !compare && eye.manual?.enabled == true)
            {
                var settings = eye.manual;
                if (tool != 0 && index == 1)
                {
                    if (coordinates.TryTargetPoint(ToUv(image, e.mousePosition), out var point) && coordinates.CanPlace(point))
                    {
                        var candidate = settings.Copy();
                        if (candidate.pins.Count == 0) candidate.BindRegions(eye);
                        candidate.pins.Add(new FBXUVEyeManualPin { position = point, origin = previewWarp?.Inverse(point) ?? point, fixedPoint = tool == 2 });
                        if (TryPreview(candidate)) { selected = candidate.pins.Count - 1; tool = 0; Commit(candidate); }
                    }
                    else error = "虹彩の内部を選んでください。外周と別パーツの瞳孔にはピンを置けません。";
                }
                else
                {
                    selected = -1;
                    if (settings.pins != null)
                        for (var i = 0; i < settings.pins.Count; i++)
                            if (settings.pins[i] != null && PinUv(index, settings.pins[i], out var uv)
                                && Vector2.Distance(ToGui(image, uv), e.mousePosition) < 10) { selected = i; break; }
                    if (selected >= 0 && !settings.pins[selected].fixedPoint)
                    {
                        draft = settings.Copy(); validDraft = TryPreview(draft);
                        dragControl = control; GUIUtility.hotControl = control;
                    }
                }
                e.Use(); repaint();
            }
        }

        internal void CancelDrag()
        {
            if (dragControl == 0) return;
            if (dragControl != 0 && GUIUtility.hotControl == dragControl) GUIUtility.hotControl = 0;
            dragControl = 0; draft = null;
            if (layer != null && eye != null && baselineMap != null) TryPreview(eye.manual ?? new FBXUVEyeManualSettings());
        }

        private void FrameEyes()
        {
            var regions = new[] { eye.iris.region, eye.target.region };
            for (var i = 0; i < 2; i++)
            {
                centers[i] = regions[i].bounds.Center;
                zoom[i] = Mathf.Clamp(.8f / Mathf.Max(regions[i].bounds.Width, regions[i].bounds.Height), 1, 64);
            }
        }

        private static Vector2 ToGui(Rect r, Vector2 uv) => new Vector2(r.x + uv.x * r.width, r.y + (1 - uv.y) * r.height);
        private static Vector2 ToUv(Rect r, Vector2 p) => new Vector2((p.x - r.x) / r.width, 1 - (p.y - r.y) / r.height);
        private static void Destroy(RenderTexture rt) { if (rt != null) { rt.Release(); Object.DestroyImmediate(rt); } }
        internal void Reset()
        {
            if (dragControl != 0 && GUIUtility.hotControl == dragControl) GUIUtility.hotControl = 0;
            dragControl = 0; draft = null; state = null; baselineMap = null; coordinates = previewWarp = null;
            selected = -1; compare = false;
            Destroy(before); Destroy(after); before = after = null; error = null;
        }
        public void Dispose() => Reset();
    }
}
