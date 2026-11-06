using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using UnityEngine;

namespace GokouKotori.FBXUVTextureTransfer
{
    internal sealed class FBXUVBodyCorrespondenceEntry : IDisposable
    {
        internal FBXUVBodyCorrespondenceEntry(string key, FBXUVBodyCorrespondence correspondence)
        {
            Key = key;
            Correspondence = correspondence;
        }

        internal FBXUVBodyCorrespondence Correspondence { get; }
        internal string Key { get; }
        internal IDisposable Gpu { get; set; }
        // Set when the cache drops the entry (eviction or Clear). A holder may still read and render it, but GPU resources
        // must not be attached to it any more: nothing would release them (FBXUVBodyOverlayGpuResources.Get).
        internal bool IsDisposed { get; private set; }

        public void Dispose()
        {
            IsDisposed = true;
            var gpu = Gpu;
            Gpu = null;
            gpu?.Dispose();
        }
    }

    internal static class FBXUVBodyCorrespondenceCache
    {
        internal const int Capacity = 2;
        internal const int AlgorithmVersion = 4;

        private static readonly List<FBXUVBodyCorrespondenceEntry> entries = new List<FBXUVBodyCorrespondenceEntry>();
        private static readonly Dictionary<string, string> failures = new Dictionary<string, string>();

        internal static FBXUVBodyCorrespondenceEntry Get(
            GameObject sourceRoot,
            Texture sourceBodyTexture,
            GameObject targetRoot,
            Texture targetTexture,
            int resolution,
            bool alignNipples = true,
            FBXUVBodyNippleShapeSelection sourceNippleShape = null,
            FBXUVBodyNippleShapeSelection targetNippleShape = null)
        {
            var key = ComputeKey(sourceRoot, sourceBodyTexture, targetRoot, targetTexture, resolution, alignNipples, sourceNippleShape, targetNippleShape);
            for (var i = 0; i < entries.Count; i++)
            {
                if (entries[i].Key != key) continue;
                var hit = entries[i];
                entries.RemoveAt(i);
                entries.Add(hit);
                return hit;
            }

            if (failures.TryGetValue(key, out var failure)) throw new ArgumentException(failure);

            FBXUVBodyCorrespondence correspondence;
            try
            {
                var source = FBXUVBodyGeometry.Create(sourceRoot, sourceBodyTexture, sourceNippleShape);
                var target = FBXUVBodyGeometry.Create(targetRoot, targetTexture, targetNippleShape);
                correspondence = FBXUVBodyCorrespondence.Build(source, target, resolution, alignNipples);
            }
            catch (ArgumentException exception)
            {
                failures[key] = exception.Message;
                throw;
            }

            var entry = new FBXUVBodyCorrespondenceEntry(key, correspondence);
            entries.Add(entry);
            while (entries.Count > Capacity)
            {
                var oldest = entries[0];
                entries.RemoveAt(0);
                oldest.Dispose();
            }
            return entry;
        }

        internal static void Clear()
        {
            foreach (var entry in entries) entry.Dispose();
            entries.Clear();
            failures.Clear();
        }

        internal static string ComputeKey(
            GameObject sourceRoot,
            Texture sourceBodyTexture,
            GameObject targetRoot,
            Texture targetTexture,
            int resolution,
            bool alignNipples = true,
            FBXUVBodyNippleShapeSelection sourceNippleShape = null,
            FBXUVBodyNippleShapeSelection targetNippleShape = null)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(AlgorithmVersion);
                writer.Write(resolution);
                writer.Write(alignNipples);
                WriteModel(writer, sourceRoot, sourceBodyTexture, sourceNippleShape);
                WriteModel(writer, targetRoot, targetTexture, targetNippleShape);
                writer.Flush();
                using (var sha = SHA256.Create())
                {
                    return BitConverter.ToString(sha.ComputeHash(stream.GetBuffer(), 0, (int)stream.Length)).Replace("-", "");
                }
            }
        }

        private static void WriteModel(BinaryWriter writer, GameObject root, Texture texture, FBXUVBodyNippleShapeSelection nippleShape)
        {
            writer.Write(texture != null ? texture.GetInstanceID() : 0);
            if (root == null)
            {
                writer.Write(-1);
                return;
            }

            var worldToRoot = root.transform.worldToLocalMatrix;
            var animator = root.GetComponent<Animator>();
            if (animator == null) animator = root.GetComponentInChildren<Animator>(true);
            if (animator != null && animator.avatar != null && animator.avatar.isHuman)
            {
                for (var bone = 0; bone < (int)HumanBodyBones.LastBone; bone++)
                {
                    var transform = animator.GetBoneTransform((HumanBodyBones)bone);
                    if (transform == null) continue;
                    writer.Write(bone);
                    Write(writer, worldToRoot * transform.localToWorldMatrix);
                }
            }
            writer.Write(-2);

            var usages = FBXUVTextureUsage.Collect(root, texture);
            // The nipple shape: the selection, what it resolves to and the shape's movement decide the landmark regions.
            var selection = nippleShape ?? new FBXUVBodyNippleShapeSelection();
            var shapeName = FBXUVBodyNippleShape.Resolve(usages, selection, out _);
            writer.Write((int)selection.mode);
            writer.Write(selection.shapeName ?? string.Empty);
            writer.Write(shapeName ?? string.Empty);
            writer.Write(usages.Count);
            foreach (var usage in usages)
            {
                var renderer = usage.Renderer;
                var mesh = usage.Mesh;
                Write(writer, worldToRoot * renderer.transform.localToWorldMatrix);
                foreach (var subMeshIndex in usage.SubMeshIndices)
                {
                    writer.Write(subMeshIndex);
                    Write(writer, mesh.GetTriangles(subMeshIndex));
                }
                writer.Write(-3);
                Write(writer, mesh.vertices);
                // The normals decide which source faces count (within 60°).
                Write(writer, mesh.normals);
                Write(writer, mesh.uv);
                var shapeIndex = shapeName == null ? -1 : mesh.GetBlendShapeIndex(shapeName);
                if (shapeIndex >= 0 && mesh.GetBlendShapeFrameCount(shapeIndex) > 0)
                {
                    var frame = mesh.GetBlendShapeFrameCount(shapeIndex) - 1;
                    var shapeDeltas = new Vector3[mesh.vertexCount];
                    mesh.GetBlendShapeFrameVertices(shapeIndex, frame, shapeDeltas, new Vector3[mesh.vertexCount], new Vector3[mesh.vertexCount]);
                    Write(writer, shapeDeltas);
                }
                else
                {
                    writer.Write(-1);
                }
                var skinned = renderer as SkinnedMeshRenderer;
                if (skinned == null) continue;
                var boneWeights = mesh.boneWeights;
                writer.Write(boneWeights.Length);
                foreach (var w in boneWeights)
                {
                    writer.Write(w.boneIndex0);
                    writer.Write(w.boneIndex1);
                    writer.Write(w.boneIndex2);
                    writer.Write(w.boneIndex3);
                    writer.Write(w.weight0);
                    writer.Write(w.weight1);
                    writer.Write(w.weight2);
                    writer.Write(w.weight3);
                }
                var bindposes = mesh.bindposes;
                writer.Write(bindposes.Length);
                foreach (var bindpose in bindposes) Write(writer, bindpose);
                var bones = skinned.bones;
                writer.Write(bones.Length);
                foreach (var bone in bones)
                {
                    if (bone == null)
                    {
                        writer.Write(false);
                        continue;
                    }
                    writer.Write(true);
                    Write(writer, worldToRoot * bone.localToWorldMatrix);
                }
            }
        }

        private static void Write(BinaryWriter writer, Matrix4x4 matrix)
        {
            for (var i = 0; i < 16; i++) writer.Write(matrix[i]);
        }

        private static void Write(BinaryWriter writer, int[] values)
        {
            writer.Write(values.Length);
            foreach (var value in values) writer.Write(value);
        }

        private static void Write(BinaryWriter writer, Vector3[] values)
        {
            writer.Write(values.Length);
            foreach (var value in values)
            {
                writer.Write(value.x);
                writer.Write(value.y);
                writer.Write(value.z);
            }
        }

        private static void Write(BinaryWriter writer, Vector2[] values)
        {
            writer.Write(values.Length);
            foreach (var value in values)
            {
                writer.Write(value.x);
                writer.Write(value.y);
            }
        }
    }
}
