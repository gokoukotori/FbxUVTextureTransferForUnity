using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace GokouKotori.FBXUVTextureTransfer.Editor
{
    internal static class FBXUVIslandPreview
    {
        internal static Vector3 ToGui(Rect rect, Vector2 uv) => new Vector3(rect.x + uv.x * rect.width, rect.y + (1 - uv.y) * rect.height, 0);

        // Caller owns the GUI clipping group, allowing the same overlay to be used at any zoom level.
        internal static void Draw(Rect rect, IReadOnlyList<FBXUVIsland> islands, int selected)
        {
            foreach (var island in islands)
            {
                Handles.color = island.id == selected ? new Color(1, .65f, .15f, .9f) : new Color(.1f, .85f, 1, .25f);
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
    }
}
