using UnityEditor;
using UnityEngine;

namespace GokouKotori.FBXUVTextureTransfer.Editor
{
    // Body overlay transfer (spec §6.2): the result in target UV over the target body texture, island outlines, the
    // uncorrected / diagnostics views and the correction pins. Selection follow, Undo, refresh requests and RecordChange
    // come from the shared layer window.
    public sealed class FBXUVBodyOverlayTransferWindow : FBXUVLayerWindow<FBXUVBodyOverlayTransferLayer>
    {
        private const string WindowTitle = "ボディ転送";
        private const float SidePanelWidth = 320f;

        private readonly FBXUVBodyPinEditor pinEditor = new FBXUVBodyPinEditor();

        internal FBXUVBodyPinEditor PinEditor => pinEditor;

        protected override string MissingLayerMessage => "設定するBody Overlay Transfer Layerを指定してください。";

        [MenuItem("Tools/Gokoukotori/FBX UV Texture Transfer/Body Overlay Editor")]
        public static void Open()
        {
            GetWindow<FBXUVBodyOverlayTransferWindow>(WindowTitle);
        }

        public static FBXUVBodyOverlayTransferWindow Open(FBXUVBodyOverlayTransferLayer layer)
        {
            var window = GetWindow<FBXUVBodyOverlayTransferWindow>(WindowTitle);
            window.SetLayer(layer);
            window.Show();
            window.Focus();
            return window;
        }

        protected override void OnEnable()
        {
            base.OnEnable();
            minSize = new Vector2(860, 600);
            pinEditor.Bind(Layer, RecordChange, Repaint);
        }

        protected override void OnDisable()
        {
            base.OnDisable();
            pinEditor.Dispose();
        }

        private void OnLostFocus() => pinEditor.CancelDrag();

        // Binding another layer cancels the drag of the previous one.
        protected override void OnLayerChanged() => pinEditor.Bind(Layer, RecordChange, Repaint);

        // Undo/redo, project and reference changes may change the saved pins or the inputs: prepare the previews again.
        internal override void OnRefresh(FBXUVRefreshKind kind) => pinEditor.Invalidate();

        // Inspector edits change the layer without an Undo event.
        protected override bool DetectSavedValueChanges() => pinEditor.IsStale(Layer);

        protected override void DrawContent()
        {
            var side = PreviewSide(position.width, position.height);
            using (new EditorGUILayout.HorizontalScope())
            {
                var area = GUILayoutUtility.GetRect(side, side, GUILayout.ExpandWidth(false), GUILayout.ExpandHeight(false));
                GUILayout.Space(8f);
                using (new EditorGUILayout.VerticalScope(GUILayout.Width(SidePanelWidth)))
                {
                    pinEditor.Draw(Layer, area, RecordChange, Repaint);
                }
            }
        }

        // The square preview next to the side panel.
        internal static float PreviewSide(float windowWidth, float windowHeight)
            => Mathf.Clamp(Mathf.Min(windowWidth - SidePanelWidth - 40f, windowHeight - 64f), 240f, 1024f);
    }
}
