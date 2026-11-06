using System;
using System.Collections.Generic;
using UnityEngine;

namespace GokouKotori.FBXUVTextureTransfer
{
    internal static class FBXUVTriangleIntersection
    {
        internal struct Point
        {
            internal double x, y;
            internal Point(double x, double y) { this.x = x; this.y = y; }
            internal Point(Vector2 p) { x = p.x; y = p.y; }
            internal Vector2 Vector => new Vector2((float)x, (float)y);
        }

        private static double Side(Point a, Point b, Point c) =>
            (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x);

        // Area only: callers retain their coordinate system and overlap tolerance.
        internal static double Area(Point a, Point b, Point c, Point d, Point e, Point f)
        {
            var polygon = new List<Point>(6) { a, b, c };
            var clip = new[] { d, e, f };
            var orientation = Math.Sign(Side(d, e, f));
            for (var edge = 0; edge < 3 && polygon.Count >= 3; edge++)
            {
                var output = new List<Point>(6);
                var u = clip[edge]; var v = clip[(edge + 1) % 3];
                for (var i = 0; i < polygon.Count; i++)
                {
                    var p = polygon[i]; var q = polygon[(i + 1) % polygon.Count];
                    var sp = Side(u, v, p) * orientation; var sq = Side(u, v, q) * orientation;
                    if (sp >= 0) output.Add(p);
                    if ((sp >= 0) != (sq >= 0))
                    {
                        var t = sp / (sp - sq);
                        output.Add(new Point(p.x + (q.x - p.x) * t, p.y + (q.y - p.y) * t));
                    }
                }
                polygon = output;
            }
            double area = 0;
            for (var i = 1; i + 1 < polygon.Count; i++) area += Side(polygon[0], polygon[i], polygon[i + 1]);
            return Math.Abs(area) * .5;
        }
    }
}
