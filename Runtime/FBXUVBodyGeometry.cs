using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using UnityEngine;

namespace GokouKotori.FBXUVTextureTransfer
{
    internal sealed class FBXUVBodyPart
    {
        internal string RendererPath;
        internal Mesh Mesh;
        internal int SubMeshIndex;
        internal int FirstTriangle;
        internal int TriangleCount;
    }

    internal sealed class FBXUVBodyGeometry
    {
        private FBXUVBodyGeometry()
        {
        }

        internal Vector3[] Positions;
        internal Vector3[] Normals;
        internal Vector2[] Uvs;
        internal int[] Triangles;
        internal int[] TrianglePart;
        internal int[] InfluenceBones;
        internal float[] InfluenceWeights;
        internal IReadOnlyDictionary<int, Vector3> Joints;
        internal float Height;
        internal float SizeFactor => Height / 1f;
        internal IReadOnlyList<FBXUVBodyPart> Parts;

        internal static int CreateCallCountForTesting;

        internal static FBXUVBodyGeometry Create(GameObject modelRoot, Texture bodyTexture)
        {
            CreateCallCountForTesting++;
            if (modelRoot == null) throw new ArgumentException("モデルが指定されていません。");
            if (bodyTexture == null) throw new ArgumentException("ボディのテクスチャが指定されていません。");

            var animator = modelRoot.GetComponent<Animator>();
            if (animator == null) animator = modelRoot.GetComponentInChildren<Animator>(true);
            if (animator == null || animator.avatar == null || !animator.avatar.isHuman)
                throw new ArgumentException("Humanoid のアバターが必要です。");

            var rootTransform = modelRoot.transform;
            var worldToRoot = rootTransform.worldToLocalMatrix;

            var byName = new Dictionary<string, Transform>();
            foreach (var transform in modelRoot.GetComponentsInChildren<Transform>(true))
            {
                if (!byName.ContainsKey(transform.name)) byName.Add(transform.name, transform);
            }

            var humanOf = new Dictionary<Transform, int>();
            var joints = new Dictionary<int, Vector3>();
            foreach (var human in animator.avatar.humanDescription.human)
            {
                var index = Array.IndexOf(HumanTrait.BoneName, human.humanName);
                if (index < 0 || !byName.TryGetValue(human.boneName, out var transform)) continue;
                if (!humanOf.ContainsKey(transform)) humanOf.Add(transform, index);
                if (!joints.ContainsKey(index)) joints.Add(index, worldToRoot.MultiplyPoint3x4(transform.position));
            }
            if (!joints.ContainsKey((int)HumanBodyBones.Hips))
                throw new ArgumentException("Humanoid のアバターに Hips の骨が見つかりません。");

            var usages = FBXUVTextureUsage.Collect(modelRoot, bodyTexture);
            if (usages.Count == 0)
                throw new ArgumentException("ボディのテクスチャを使うサブメッシュが見つかりません。");

            var positions = new List<Vector3>();
            var normals = new List<Vector3>();
            var uvs = new List<Vector2>();
            var triangles = new List<int>();
            var trianglePart = new List<int>();
            var influenceBones = new List<int>();
            var influenceWeights = new List<float>();
            var parts = new List<FBXUVBodyPart>();

            foreach (var usage in usages)
            {
                var renderer = usage.Renderer;
                var mesh = usage.Mesh;
                var path = HierarchyPath(rootTransform, renderer.transform);
                var vertexBase = positions.Count;
                var vertices = mesh.vertices;
                var meshNormals = mesh.normals;
                var meshUvs = mesh.uv;
                var vertexCount = vertices.Length;

                BoneWeight[] weights;
                Matrix4x4[] boneLocalToWorld;
                Matrix4x4[] bindposes;
                int[] boneHuman;
                var skinned = renderer as SkinnedMeshRenderer;
                var boneWeights = skinned != null ? mesh.boneWeights : null;
                if (skinned != null && boneWeights.Length == vertexCount && vertexCount > 0)
                {
                    var bones = skinned.bones;
                    var meshBindposes = mesh.bindposes;
                    if (bones.Length != meshBindposes.Length)
                        throw new ArgumentException($"Renderer {path} のボーン数とバインドポーズ数が一致しません。");
                    boneLocalToWorld = new Matrix4x4[bones.Length];
                    boneHuman = new int[bones.Length];
                    var missingBones = new bool[bones.Length];
                    for (var i = 0; i < bones.Length; i++)
                    {
                        if (bones[i] == null)
                        {
                            // Some avatars keep unused, empty bone slots (Shinano); only a bone that a vertex really uses is an error.
                            missingBones[i] = true;
                            boneLocalToWorld[i] = Matrix4x4.identity;
                            boneHuman[i] = (int)HumanBodyBones.Hips;
                            continue;
                        }
                        boneLocalToWorld[i] = bones[i].localToWorldMatrix;
                        boneHuman[i] = NearestHumanBone(bones[i], humanOf);
                    }
                    foreach (var w in boneWeights)
                    {
                        if (UsesMissingBone(w.boneIndex0, w.weight0, missingBones) || UsesMissingBone(w.boneIndex1, w.weight1, missingBones)
                            || UsesMissingBone(w.boneIndex2, w.weight2, missingBones) || UsesMissingBone(w.boneIndex3, w.weight3, missingBones))
                            throw new ArgumentException($"Renderer {path} のボーンが欠けています。");
                    }
                    weights = boneWeights;
                    bindposes = meshBindposes;
                }
                else
                {
                    // MeshRenderer, or a SkinnedMeshRenderer without bone weights: rigid on the renderer transform.
                    boneLocalToWorld = new[] { renderer.transform.localToWorldMatrix };
                    bindposes = new[] { Matrix4x4.identity };
                    boneHuman = new[] { NearestHumanBone(renderer.transform, humanOf) };
                    weights = new BoneWeight[vertexCount];
                    for (var i = 0; i < vertexCount; i++) weights[i] = new BoneWeight { boneIndex0 = 0, weight0 = 1f };
                }

                var skinnedPositions = Skin(vertices, weights, boneLocalToWorld, bindposes, worldToRoot);
                Vector3[] skinnedNormals;
                if (meshNormals.Length == vertexCount)
                    skinnedNormals = SkinNormals(meshNormals, weights, boneLocalToWorld, bindposes, worldToRoot);
                else
                    skinnedNormals = RecalculateNormals(skinnedPositions, usage, mesh);

                positions.AddRange(skinnedPositions);
                normals.AddRange(skinnedNormals);
                for (var i = 0; i < vertexCount; i++) uvs.Add(i < meshUvs.Length ? meshUvs[i] : Vector2.zero);
                for (var i = 0; i < vertexCount; i++)
                {
                    var w = weights[i];
                    influenceBones.Add(HumanBoneOf(boneHuman, w.boneIndex0));
                    influenceBones.Add(HumanBoneOf(boneHuman, w.boneIndex1));
                    influenceBones.Add(HumanBoneOf(boneHuman, w.boneIndex2));
                    influenceBones.Add(HumanBoneOf(boneHuman, w.boneIndex3));
                    influenceWeights.Add(w.weight0);
                    influenceWeights.Add(w.weight1);
                    influenceWeights.Add(w.weight2);
                    influenceWeights.Add(w.weight3);
                }

                foreach (var subMeshIndex in usage.SubMeshIndices)
                {
                    var subTriangles = mesh.GetTriangles(subMeshIndex);
                    var part = new FBXUVBodyPart
                    {
                        RendererPath = path,
                        Mesh = mesh,
                        SubMeshIndex = subMeshIndex,
                        FirstTriangle = triangles.Count / 3,
                        TriangleCount = subTriangles.Length / 3,
                    };
                    var partIndex = parts.Count;
                    parts.Add(part);
                    foreach (var index in subTriangles) triangles.Add(index + vertexBase);
                    for (var i = 0; i < part.TriangleCount; i++) trianglePart.Add(partIndex);
                }
            }

            var geometry = new FBXUVBodyGeometry
            {
                Positions = positions.ToArray(),
                Normals = normals.ToArray(),
                Uvs = uvs.ToArray(),
                Triangles = triangles.ToArray(),
                TrianglePart = trianglePart.ToArray(),
                InfluenceBones = influenceBones.ToArray(),
                InfluenceWeights = influenceWeights.ToArray(),
                Joints = new ReadOnlyDictionary<int, Vector3>(joints),
                Parts = parts.AsReadOnly(),
            };

            if (geometry.Triangles.Length == 0)
                throw new ArgumentException("ボディのテクスチャを使う三角形が見つかりません。");

            var minY = float.PositiveInfinity;
            var maxY = float.NegativeInfinity;
            foreach (var index in geometry.Triangles)
            {
                var y = geometry.Positions[index].y;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
            var headY = joints.TryGetValue((int)HumanBodyBones.Head, out var head) ? head.y : maxY;
            geometry.Height = headY - minY;
            return geometry;
        }

        internal static Vector3[] Skin(
            Vector3[] vertices,
            BoneWeight[] weights,
            Matrix4x4[] boneLocalToWorld,
            Matrix4x4[] bindposes,
            Matrix4x4 worldToRoot)
        {
            return SkinCore(vertices, weights, boneLocalToWorld, bindposes, worldToRoot, true);
        }

        private static Vector3[] SkinNormals(
            Vector3[] normals,
            BoneWeight[] weights,
            Matrix4x4[] boneLocalToWorld,
            Matrix4x4[] bindposes,
            Matrix4x4 worldToRoot)
        {
            var result = SkinCore(normals, weights, boneLocalToWorld, bindposes, worldToRoot, false);
            for (var i = 0; i < result.Length; i++) result[i] = result[i].normalized;
            return result;
        }

        private static Vector3[] SkinCore(
            Vector3[] source,
            BoneWeight[] weights,
            Matrix4x4[] boneLocalToWorld,
            Matrix4x4[] bindposes,
            Matrix4x4 worldToRoot,
            bool asPoint)
        {
            var count = source.Length;
            var skinMatrices = new Matrix4x4[boneLocalToWorld.Length];
            for (var i = 0; i < skinMatrices.Length; i++) skinMatrices[i] = boneLocalToWorld[i] * bindposes[i];

            var result = new Vector3[count];
            for (var v = 0; v < count; v++)
            {
                var w = weights[v];
                var sum = Vector3.zero;
                var total = 0f;
                Accumulate(skinMatrices, w.boneIndex0, w.weight0, source[v], asPoint, ref sum, ref total);
                Accumulate(skinMatrices, w.boneIndex1, w.weight1, source[v], asPoint, ref sum, ref total);
                Accumulate(skinMatrices, w.boneIndex2, w.weight2, source[v], asPoint, ref sum, ref total);
                Accumulate(skinMatrices, w.boneIndex3, w.weight3, source[v], asPoint, ref sum, ref total);
                if (total > 1e-8f) sum /= total;
                else sum = source[v];
                result[v] = asPoint ? worldToRoot.MultiplyPoint3x4(sum) : worldToRoot.MultiplyVector(sum);
            }
            return result;
        }

        private static void Accumulate(
            Matrix4x4[] skinMatrices,
            int boneIndex,
            float weight,
            Vector3 value,
            bool asPoint,
            ref Vector3 sum,
            ref float total)
        {
            if (weight <= 0f) return;
            var matrix = skinMatrices[boneIndex];
            sum += weight * (asPoint ? matrix.MultiplyPoint3x4(value) : matrix.MultiplyVector(value));
            total += weight;
        }

        private static Vector3[] RecalculateNormals(Vector3[] positions, FBXUVTextureSubmeshes usage, Mesh mesh)
        {
            var normals = new Vector3[positions.Length];
            foreach (var subMeshIndex in usage.SubMeshIndices)
            {
                var triangles = mesh.GetTriangles(subMeshIndex);
                for (var i = 0; i + 2 < triangles.Length; i += 3)
                {
                    var a = triangles[i];
                    var b = triangles[i + 1];
                    var c = triangles[i + 2];
                    var normal = Vector3.Cross(positions[b] - positions[a], positions[c] - positions[a]);
                    normals[a] += normal;
                    normals[b] += normal;
                    normals[c] += normal;
                }
            }
            for (var i = 0; i < normals.Length; i++) normals[i] = normals[i].normalized;
            return normals;
        }

        // A bone that a vertex really uses (weight > 0) but that is not there: outside bones[] or an empty slot.
        private static bool UsesMissingBone(int index, float weight, bool[] missingBones)
            => weight > 0f && (index < 0 || index >= missingBones.Length || missingBones[index]);

        // A slot without weight may hold any index (weighted ones are checked by UsesMissingBone); it counts as Hips.
        private static int HumanBoneOf(int[] boneHuman, int index)
            => index >= 0 && index < boneHuman.Length ? boneHuman[index] : (int)HumanBodyBones.Hips;

        private static int NearestHumanBone(Transform transform, Dictionary<Transform, int> humanOf)
        {
            for (var current = transform; current != null; current = current.parent)
            {
                if (humanOf.TryGetValue(current, out var found)) return found;
            }
            return (int)HumanBodyBones.Hips;
        }

        private static string HierarchyPath(Transform root, Transform transform)
        {
            var names = new List<string>();
            for (var current = transform; current != null && current != root; current = current.parent)
                names.Add(current.name);
            names.Reverse();
            return string.Join("/", names);
        }

        internal static Texture SuggestBodyTexture(GameObject modelRoot)
        {
            Texture best = null;
            var bestTriangles = -1;
            foreach (var item in FBXUVRendererMeshUtility.Collect(modelRoot))
            {
                var mesh = item.Mesh;
                if (mesh.subMeshCount <= 0) continue;
                var materials = item.Renderer.sharedMaterials;
                for (var materialIndex = 0; materialIndex < materials.Length; materialIndex++)
                {
                    var material = materials[materialIndex];
                    if (material == null) continue;
                    if (material.name.IndexOf("body", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    var texture = material.mainTexture;
                    if (texture == null) continue;
                    var subMeshIndex = Mathf.Min(materialIndex, mesh.subMeshCount - 1);
                    var triangleCount = (int)(mesh.GetIndexCount(subMeshIndex) / 3);
                    if (triangleCount <= bestTriangles) continue;
                    best = texture;
                    bestTriangles = triangleCount;
                }
            }
            return best;
        }

        internal static List<Texture> CandidateBodyTextures(GameObject modelRoot)
        {
            var result = new List<Texture>();
            foreach (var item in FBXUVRendererMeshUtility.Collect(modelRoot))
            {
                foreach (var material in item.Renderer.sharedMaterials)
                {
                    if (material == null) continue;
                    var texture = material.mainTexture;
                    if (texture != null && !result.Contains(texture)) result.Add(texture);
                }
            }
            return result;
        }
    }
}
