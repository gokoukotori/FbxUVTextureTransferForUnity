using System.Collections.Generic;

namespace GokouKotori.FBXUVTextureTransfer
{
    // Value copies of triangle lists for caches. Compare values, not object identity: editor Undo and scripts can
    // mutate the saved lists and their triangles in place.
    internal static class FBXUVTriangleSnapshot
    {
        internal static FBXUVTriangle[] Copy(List<FBXUVTriangle> triangles) =>
            triangles?.ConvertAll(t => t == null ? null : new FBXUVTriangle(t.index, t.a, t.b, t.c)).ToArray();

        internal static bool Same(IReadOnlyList<FBXUVTriangle> saved, IReadOnlyList<FBXUVTriangle> current)
        {
            if (saved == null || current == null) return saved == null && current == null;
            if (saved.Count != current.Count) return false;
            for (var i = 0; i < saved.Count; i++)
            {
                var a = saved[i]; var b = current[i];
                if (a == null || b == null) { if (a != b) return false; }
                else if (a.index != b.index || !a.a.Equals(b.a) || !a.b.Equals(b.b) || !a.c.Equals(b.c)) return false;
            }
            return true;
        }
    }
}
