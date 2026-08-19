using System;
using System.Collections.Generic;
using UnityEngine;

namespace GokouKotori.FBXUVTextureTransfer
{
    public enum FBXUVTransferOrientation
    {
        Preserve = 0,
        FlipHorizontal = 2,
        FlipVertical = 3,
        Rotate180 = 4,
    }

    [Serializable]
    public sealed class FBXUVTriangle
    {
        public int index;
        public Vector2 a;
        public Vector2 b;
        public Vector2 c;

        public int Index { get { return index; } set { index = value; } }
        public Vector2 A { get { return a; } set { a = value; } }
        public Vector2 B { get { return b; } set { b = value; } }
        public Vector2 C { get { return c; } set { c = value; } }

        public FBXUVTriangle()
        {
        }

        public FBXUVTriangle(int index, Vector2 a, Vector2 b, Vector2 c)
        {
            this.index = index;
            this.a = a;
            this.b = b;
            this.c = c;
        }

        public Vector2 GetPoint(int pointIndex)
        {
            switch (pointIndex)
            {
                case 0: return a;
                case 1: return b;
                case 2: return c;
                default: throw new ArgumentOutOfRangeException(nameof(pointIndex));
            }
        }
    }

    [Serializable]
    public struct FBXUVBounds
    {
        public float minU;
        public float minV;
        public float maxU;
        public float maxV;

        public float MinU { get { return minU; } set { minU = value; } }
        public float MinV { get { return minV; } set { minV = value; } }
        public float MaxU { get { return maxU; } set { maxU = value; } }
        public float MaxV { get { return maxV; } set { maxV = value; } }
        public float Width { get { return Mathf.Max(0f, maxU - minU); } }
        public float Height { get { return Mathf.Max(0f, maxV - minV); } }
        public bool IsValid { get { return maxU > minU && maxV > minV; } }
        public Vector2 Center { get { return new Vector2((minU + maxU) * 0.5f, (minV + maxV) * 0.5f); } }

        public FBXUVBounds(float minU, float minV, float maxU, float maxV)
        {
            this.minU = Mathf.Min(minU, maxU);
            this.minV = Mathf.Min(minV, maxV);
            this.maxU = Mathf.Max(minU, maxU);
            this.maxV = Mathf.Max(minV, maxV);
        }

        public bool Contains(Vector2 point, float epsilon = 0.000001f)
        {
            return point.x >= minU - epsilon && point.x <= maxU + epsilon
                && point.y >= minV - epsilon && point.y <= maxV + epsilon;
        }

        public static FBXUVBounds FromTriangles(IReadOnlyList<FBXUVTriangle> triangles)
        {
            if (triangles == null || triangles.Count == 0)
            {
                return default(FBXUVBounds);
            }

            var minU = float.PositiveInfinity;
            var minV = float.PositiveInfinity;
            var maxU = float.NegativeInfinity;
            var maxV = float.NegativeInfinity;
            for (var triangleIndex = 0; triangleIndex < triangles.Count; triangleIndex++)
            {
                var triangle = triangles[triangleIndex];
                if (triangle == null) continue;
                for (var pointIndex = 0; pointIndex < 3; pointIndex++)
                {
                    var point = triangle.GetPoint(pointIndex);
                    minU = Mathf.Min(minU, point.x);
                    minV = Mathf.Min(minV, point.y);
                    maxU = Mathf.Max(maxU, point.x);
                    maxV = Mathf.Max(maxV, point.y);
                }
            }

            if (float.IsInfinity(minU)) return default(FBXUVBounds);
            return new FBXUVBounds(minU, minV, maxU, maxV);
        }
    }

    [Serializable]
    public sealed class FBXUVTransferRegion
    {
        public Mesh mesh;
        public int subMeshIndex;
        public int uvChannel;
        public int islandId = -1;
        public List<FBXUVTriangle> triangles = new List<FBXUVTriangle>();
        public FBXUVBounds bounds;
        public string meshHash = string.Empty;

        public Mesh Mesh { get { return mesh; } set { mesh = value; } }
        public int SubMeshIndex { get { return subMeshIndex; } set { subMeshIndex = value; } }
        public int UvChannel { get { return uvChannel; } set { uvChannel = value; } }
        public int IslandId { get { return islandId; } set { islandId = value; } }
        public List<FBXUVTriangle> Triangles { get { return triangles; } set { triangles = value ?? new List<FBXUVTriangle>(); } }
        public FBXUVBounds Bounds { get { return bounds; } set { bounds = value; } }
        public string MeshHash { get { return meshHash; } set { meshHash = value; } }
    }

    [Serializable]
    public sealed class FBXUVRegionBinding : ISerializationCallbackReceiver
    {
        private const int RemovedOrientationValue = 1;

        public string name = string.Empty;
        public bool enabled = true;
        public FBXUVTransferRegion sourceRegion = new FBXUVTransferRegion();
        public FBXUVTransferRegion targetRegion = new FBXUVTransferRegion();
        public FBXUVTransferOrientation orientation = FBXUVTransferOrientation.Preserve;

        public string Name { get { return name; } set { name = value; } }
        public bool Enabled { get { return enabled; } set { enabled = value; } }
        public FBXUVTransferRegion SourceRegion { get { return sourceRegion; } set { sourceRegion = value; } }
        public FBXUVTransferRegion TargetRegion { get { return targetRegion; } set { targetRegion = value; } }
        public FBXUVTransferOrientation Orientation { get { return orientation; } set { orientation = value; } }

        public void OnBeforeSerialize()
        {
        }

        public void OnAfterDeserialize()
        {
            if ((int)orientation == RemovedOrientationValue)
            {
                orientation = FBXUVTransferOrientation.Preserve;
            }
        }
    }
}
