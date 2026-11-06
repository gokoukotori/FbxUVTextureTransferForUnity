using System;
using System.Collections.Generic;
using UnityEngine;

namespace GokouKotori.FBXUVTextureTransfer
{
    // One owner per Render/RenderRegion call; all regions in a layer reuse its meshes.
    internal sealed class FBXUVRenderResources : IDisposable
    {
        internal readonly Material Material;
        internal readonly Mesh TransferMesh;
        internal readonly Mesh MaskMesh;
        internal readonly MeshBuffers Buffers = new MeshBuffers();
        internal readonly FBXUVPixelProcessor Pixels;

        internal FBXUVRenderResources(Shader shader, ComputeShader computeShader)
        {
            try
            {
                Material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
                TransferMesh = CreateMesh("FBXUV Transfer Mesh");
                MaskMesh = CreateMesh("FBXUV Mask Mesh");
                Pixels = new FBXUVPixelProcessor(computeShader);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        private static Mesh CreateMesh(string name)
        {
            return new Mesh { name = name, hideFlags = HideFlags.HideAndDontSave };
        }

        public void Dispose()
        {
            if (MaskMesh != null) UnityEngine.Object.DestroyImmediate(MaskMesh);
            if (TransferMesh != null) UnityEngine.Object.DestroyImmediate(TransferMesh);
            if (Material != null) UnityEngine.Object.DestroyImmediate(Material);
        }

        internal sealed class MeshBuffers
        {
            public readonly List<Vector3> Positions = new List<Vector3>();
            public readonly List<Vector2> Uvs = new List<Vector2>();
            public readonly List<int> Indices = new List<int>();

            public void Prepare(int capacity)
            {
                Positions.Clear();
                Uvs.Clear();
                Indices.Clear();
                EnsureCapacity(Positions, capacity);
                EnsureCapacity(Uvs, capacity);
                EnsureCapacity(Indices, capacity);
            }

            private static void EnsureCapacity<T>(List<T> list, int capacity)
            {
                if (list.Capacity < capacity) list.Capacity = capacity;
            }
        }

    }
}
