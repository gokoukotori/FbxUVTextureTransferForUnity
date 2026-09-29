using UnityEditor;
using UnityEngine;

namespace GokouKotori.FBXUVTextureTransfer.Editor
{
    // View-only state: navigation must never modify a layer or create an Undo entry.
    internal sealed class FBXUVPreviewViewport
    {
        internal float Zoom { get; private set; } = 1;
        internal Vector2 Center { get; private set; } = Vector2.one * .5f;

        internal Rect ImageRect(Rect viewport) => new Rect(
            viewport.center.x - Center.x * viewport.width * Zoom,
            viewport.center.y - (1 - Center.y) * viewport.height * Zoom,
            viewport.width * Zoom, viewport.height * Zoom);

        internal static Vector2 ToUv(Rect image, Vector2 point) => new Vector2(
            (point.x - image.x) / image.width, 1 - (point.y - image.y) / image.height);

        internal bool HandleInput(Rect viewport)
        {
            var current = Event.current;
            if (GUIUtility.hotControl != 0 || !viewport.Contains(current.mousePosition)) return false;
            if (current.type == EventType.ScrollWheel)
                Zoom = Mathf.Clamp(Zoom * Mathf.Exp(-current.delta.y * .12f), 1, 64);
            else if (current.type == EventType.MouseDrag && current.button == 2)
                Center += new Vector2(-current.delta.x / viewport.width, current.delta.y / viewport.height) / Zoom;
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
                    Center = Vector2.one * .5f;
                    Zoom = 1;
                    changed = true;
                }
                if (GUILayout.Button("候補を拡大") && selectedIsland != null)
                {
                    Center = selectedIsland.bounds.Center;
                    Zoom = Mathf.Clamp(.8f / Mathf.Max(selectedIsland.bounds.Width, selectedIsland.bounds.Height), 1, 64);
                    changed = true;
                }
            }
            return changed;
        }
    }
}
