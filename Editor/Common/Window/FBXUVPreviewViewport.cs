using UnityEditor;
using UnityEngine;

namespace GokouKotori.FBXUVTextureTransfer.Editor
{
    // View-only state: navigation must never modify a layer or create an Undo entry.
    internal sealed class FBXUVPreviewViewport
    {
        internal float Zoom { get; private set; } = 1;
        internal Vector2 Center { get; private set; } = Vector2.one * .5f;

        // The image is a square whose side is the viewport's shorter side, so a wide frame never stretches it.
        internal Rect ImageRect(Rect viewport)
        {
            var side = Mathf.Min(viewport.width, viewport.height);
            return new Rect(
                viewport.center.x - Center.x * side * Zoom,
                viewport.center.y - (1 - Center.y) * side * Zoom,
                side * Zoom, side * Zoom);
        }

        internal void ShowAll()
        {
            Center = Vector2.one * .5f;
            Zoom = 1;
        }

        internal void Frame(FBXUVBounds bounds)
        {
            Center = bounds.Center;
            Zoom = Mathf.Clamp(.8f / Mathf.Max(bounds.Width, bounds.Height), 1, 64);
        }

        internal static Vector2 ToUv(Rect image, Vector2 point) => new Vector2(
            (point.x - image.x) / image.width, 1 - (point.y - image.y) / image.height);

        internal static Vector3 ToGui(Rect image, Vector2 uv) => new Vector3(
            image.x + uv.x * image.width, image.y + (1 - uv.y) * image.height, 0);

        internal bool HandleInput(Rect viewport)
        {
            var current = Event.current;
            if (GUIUtility.hotControl != 0 || !viewport.Contains(current.mousePosition)) return false;
            var side = Mathf.Min(viewport.width, viewport.height);
            if (current.type == EventType.ScrollWheel)
                Zoom = Mathf.Clamp(Zoom * Mathf.Exp(-current.delta.y * .12f), 1, 64);
            else if (current.type == EventType.MouseDrag && current.button == 2)
                Center += new Vector2(-current.delta.x / side, current.delta.y / side) / Zoom;
            else return false;
            current.Use();
            return true;
        }

        internal bool DrawControls(FBXUVIsland selectedIsland)
        {
            var changed = false;
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("全体"))
                {
                    ShowAll();
                    changed = true;
                }
                if (GUILayout.Button("選択中の島を拡大") && selectedIsland != null)
                {
                    Frame(selectedIsland.bounds);
                    changed = true;
                }
            }
            return changed;
        }
    }
}
