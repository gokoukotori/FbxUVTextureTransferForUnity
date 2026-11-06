using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace GokouKotori.FBXUVTextureTransfer.Editor
{
    internal static class FBXUVIslandPreview
    {
        private static readonly Color islandColor = new Color(.1f, .85f, 1, .25f);
        private static readonly Color highlightColor = new Color(1, .65f, .15f, .9f);
        private static readonly Color frameColor = new Color(1, 1, 1, .3f);

        // Caller owns the Handles.BeginGUI/EndGUI pair and the GUI clipping group, allowing the same overlay to be used at any zoom level.
        internal static void Draw(Rect image, IReadOnlyList<FBXUVIsland> islands, int highlightedIslandId)
        {
            if (Event.current.type != EventType.Repaint) return;

            FBXUVIsland highlighted = null;
            Handles.color = islandColor;
            if (islands != null)
            {
                foreach (var island in islands)
                {
                    if (island.id == highlightedIslandId)
                    {
                        highlighted = island;
                        continue;
                    }

                    Handles.DrawLines(BuildLineList(image, island));
                }
            }

            if (highlighted != null)
            {
                Handles.color = highlightColor;
                var points = new Vector3[4];
                foreach (var triangle in highlighted.triangles)
                {
                    points[0] = FBXUVPreviewViewport.ToGui(image, triangle.a);
                    points[1] = FBXUVPreviewViewport.ToGui(image, triangle.b);
                    points[2] = FBXUVPreviewViewport.ToGui(image, triangle.c);
                    points[3] = points[0];
                    Handles.DrawAAPolyLine(2.2f, points);
                }
            }

            Handles.color = frameColor;
            Handles.DrawAAPolyLine(1f,
                new Vector3(image.x, image.y),
                new Vector3(image.xMax, image.y),
                new Vector3(image.xMax, image.yMax),
                new Vector3(image.x, image.yMax),
                new Vector3(image.x, image.y));
        }

        // Three edges per triangle as a Handles.DrawLines vertex list (a-b, b-c, c-a).
        internal static Vector3[] BuildLineList(Rect image, FBXUVIsland island)
        {
            var lines = new Vector3[island.triangles.Count * 6];
            for (var i = 0; i < island.triangles.Count; i++)
            {
                var triangle = island.triangles[i];
                var a = FBXUVPreviewViewport.ToGui(image, triangle.a);
                var b = FBXUVPreviewViewport.ToGui(image, triangle.b);
                var c = FBXUVPreviewViewport.ToGui(image, triangle.c);
                lines[i * 6] = a;
                lines[i * 6 + 1] = b;
                lines[i * 6 + 2] = b;
                lines[i * 6 + 3] = c;
                lines[i * 6 + 4] = c;
                lines[i * 6 + 5] = a;
            }

            return lines;
        }
    }
}
