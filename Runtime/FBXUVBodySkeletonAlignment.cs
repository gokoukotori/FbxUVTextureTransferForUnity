using System;
using System.Collections.Generic;
using UnityEngine;

namespace GokouKotori.FBXUVTextureTransfer
{
    internal static class FBXUVBodySkeletonAlignment
    {
        private const float MinRatio = 0.6f;
        private const float MaxRatio = 1.6f;
        private const float ShortBoneLength = 0.06f;
        private const float UpReferenceThreshold = 0.9f;

        private struct Frame
        {
            internal Vector3 Origin;
            internal Vector3 A;
            internal Vector3 F;
            internal Vector3 U;
            internal float Length;
            internal bool Leaf;
        }

        private static readonly HumanBodyBones[] requiredBones =
        {
            HumanBodyBones.Hips, HumanBodyBones.Spine, HumanBodyBones.Head,
            HumanBodyBones.LeftUpperArm, HumanBodyBones.RightUpperArm,
            HumanBodyBones.LeftLowerArm, HumanBodyBones.RightLowerArm,
            HumanBodyBones.LeftHand, HumanBodyBones.RightHand,
            HumanBodyBones.LeftUpperLeg, HumanBodyBones.RightUpperLeg,
            HumanBodyBones.LeftLowerLeg, HumanBodyBones.RightLowerLeg,
            HumanBodyBones.LeftFoot, HumanBodyBones.RightFoot,
        };

        private static readonly Dictionary<int, int[]> childPriority = BuildChildPriority();

        internal static IReadOnlyList<string> MissingRequiredBones(IReadOnlyDictionary<int, Vector3> sourceJoints, IReadOnlyDictionary<int, Vector3> targetJoints)
        {
            var missing = new List<string>();
            foreach (var bone in requiredBones)
            {
                var index = (int)bone;
                if (sourceJoints != null && sourceJoints.ContainsKey(index) && targetJoints != null && targetJoints.ContainsKey(index)) continue;
                missing.Add(HumanTrait.BoneName[index]);
            }
            return missing;
        }

        internal static bool UseUpReference(int bone, Vector3 sourceAxis, Vector3 targetAxis)
        {
            if (IsFootLike(bone)) return true;
            return Mathf.Abs(sourceAxis.z) > UpReferenceThreshold || Mathf.Abs(targetAxis.z) > UpReferenceThreshold;
        }

        internal static Vector3[] Align(FBXUVBodyGeometry source, FBXUVBodyGeometry target)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (target == null) throw new ArgumentNullException(nameof(target));
            var missing = MissingRequiredBones(source.Joints, target.Joints);
            if (missing.Count > 0) throw new ArgumentException("転送元と転送先に共通の骨が足りません: " + string.Join(", ", missing));

            const int boneCount = (int)HumanBodyBones.LastBone;
            var common = new bool[boneCount];
            foreach (var key in source.Joints.Keys)
            {
                if (key >= 0 && key < boneCount && target.Joints.ContainsKey(key)) common[key] = true;
            }

            var remap = new int[boneCount];
            for (var bone = 0; bone < boneCount; bone++)
            {
                var current = bone;
                while (current >= 0 && !common[current]) current = HumanTrait.GetParentBone(current);
                remap[bone] = current < 0 ? (int)HumanBodyBones.Hips : current;
            }

            var heightRatio = source.Height > 1e-6f ? target.Height / source.Height : 1f;
            var order = new List<int>();
            for (var bone = 0; bone < boneCount; bone++)
            {
                if (common[bone]) order.Add(bone);
            }
            order.Sort((x, y) => Depth(x).CompareTo(Depth(y)));

            var sourceFrames = new Frame[boneCount];
            var targetFrames = new Frame[boneCount];
            var ratios = new Vector2[boneCount];
            foreach (var bone in order)
            {
                var child = FindChild(bone, common, source.Joints, target.Joints);
                if (child >= 0)
                {
                    var sourceVector = source.Joints[child] - source.Joints[bone];
                    var targetVector = target.Joints[child] - target.Joints[bone];
                    var sourceAxis = sourceVector.normalized;
                    var targetAxis = targetVector.normalized;
                    var up = UseUpReference(bone, sourceAxis, targetAxis);
                    var reference = up ? Vector3.up : Vector3.forward;
                    sourceFrames[bone] = MakeFrame(source.Joints[bone], sourceAxis, sourceVector.magnitude, reference);
                    targetFrames[bone] = MakeFrame(target.Joints[bone], targetAxis, targetVector.magnitude, reference);
                }
                else
                {
                    var parent = FindFrameParent(bone, common, sourceFrames, targetFrames);
                    sourceFrames[bone] = sourceFrames[parent];
                    targetFrames[bone] = targetFrames[parent];
                    sourceFrames[bone].Origin = source.Joints[bone];
                    targetFrames[bone].Origin = target.Joints[bone];
                    sourceFrames[bone].Leaf = true;
                    targetFrames[bone].Leaf = true;
                }
            }

            foreach (var bone in order)
            {
                var owner = bone;
                if (sourceFrames[bone].Leaf) owner = FindFrameParent(bone, common, sourceFrames, targetFrames);
                // The length ratio is taken relative to the height ratio so that a uniformly scaled copy
                // maps back to itself; a bone shorter than 6cm (in target scale) falls back to the height ratio.
                var scaledSourceLength = sourceFrames[owner].Length * heightRatio;
                var targetLength = targetFrames[owner].Length;
                var axial = heightRatio;
                if (Mathf.Min(scaledSourceLength, targetLength) >= ShortBoneLength)
                {
                    axial = heightRatio * Mathf.Clamp(targetLength / Mathf.Max(scaledSourceLength, 1e-6f), MinRatio, MaxRatio);
                }
                ratios[bone] = new Vector2(axial, heightRatio);
            }

            var positions = source.Positions;
            var result = new Vector3[positions.Length];
            for (var v = 0; v < positions.Length; v++)
            {
                var accumulated = Vector3.zero;
                var sum = 0f;
                for (var k = 0; k < 4; k++)
                {
                    var weight = source.InfluenceWeights[v * 4 + k];
                    if (weight <= 0f) continue;
                    var influence = source.InfluenceBones[v * 4 + k];
                    var bone = influence >= 0 && influence < boneCount ? remap[influence] : (int)HumanBodyBones.Hips;
                    accumulated += MapPoint(sourceFrames[bone], targetFrames[bone], ratios[bone], positions[v]) * weight;
                    sum += weight;
                }
                if (sum > 0f)
                {
                    result[v] = accumulated / sum;
                }
                else
                {
                    var hips = (int)HumanBodyBones.Hips;
                    result[v] = MapPoint(sourceFrames[hips], targetFrames[hips], ratios[hips], positions[v]);
                }
            }
            return result;
        }

        private static Vector3 MapPoint(Frame source, Frame target, Vector2 ratio, Vector3 point)
        {
            var d = point - source.Origin;
            return target.Origin
                + target.A * (Vector3.Dot(d, source.A) * ratio.x)
                + target.F * (Vector3.Dot(d, source.F) * ratio.y)
                + target.U * (Vector3.Dot(d, source.U) * ratio.y);
        }

        private static Frame MakeFrame(Vector3 origin, Vector3 axis, float length, Vector3 reference)
        {
            var f = reference - Vector3.Dot(reference, axis) * axis;
            if (f.sqrMagnitude < 1e-6f)
            {
                var other = Mathf.Abs(axis.y) < 0.9f ? Vector3.up : Vector3.right;
                f = other - Vector3.Dot(other, axis) * axis;
            }
            f.Normalize();
            return new Frame { Origin = origin, A = axis, F = f, U = Vector3.Cross(axis, f), Length = length };
        }

        private static int FindChild(int bone, bool[] common, IReadOnlyDictionary<int, Vector3> sourceJoints, IReadOnlyDictionary<int, Vector3> targetJoints)
        {
            if (!childPriority.TryGetValue(bone, out var candidates)) return -1;
            foreach (var candidate in candidates)
            {
                if (!common[candidate]) continue;
                if ((sourceJoints[candidate] - sourceJoints[bone]).sqrMagnitude < 1e-10f) continue;
                if ((targetJoints[candidate] - targetJoints[bone]).sqrMagnitude < 1e-10f) continue;
                return candidate;
            }
            return -1;
        }

        private static int FindFrameParent(int bone, bool[] common, Frame[] sourceFrames, Frame[] targetFrames)
        {
            var parent = HumanTrait.GetParentBone(bone);
            while (parent >= 0 && (!common[parent] || sourceFrames[parent].Leaf || sourceFrames[parent].Length <= 0f || targetFrames[parent].Length <= 0f))
            {
                parent = HumanTrait.GetParentBone(parent);
            }
            return parent < 0 ? (int)HumanBodyBones.Hips : parent;
        }

        private static int Depth(int bone)
        {
            var depth = 0;
            for (var parent = HumanTrait.GetParentBone(bone); parent >= 0; parent = HumanTrait.GetParentBone(parent)) depth++;
            return depth;
        }

        private static bool IsFootLike(int bone)
        {
            return bone == (int)HumanBodyBones.LeftFoot || bone == (int)HumanBodyBones.RightFoot
                || bone == (int)HumanBodyBones.LeftToes || bone == (int)HumanBodyBones.RightToes;
        }

        private static Dictionary<int, int[]> BuildChildPriority()
        {
            var map = new Dictionary<int, int[]>();
            void Set(HumanBodyBones bone, params HumanBodyBones[] children)
            {
                var array = new int[children.Length];
                for (var i = 0; i < children.Length; i++) array[i] = (int)children[i];
                map[(int)bone] = array;
            }
            Set(HumanBodyBones.Hips, HumanBodyBones.Spine);
            Set(HumanBodyBones.Spine, HumanBodyBones.Chest, HumanBodyBones.UpperChest, HumanBodyBones.Neck);
            Set(HumanBodyBones.Chest, HumanBodyBones.UpperChest, HumanBodyBones.Neck);
            Set(HumanBodyBones.UpperChest, HumanBodyBones.Neck);
            Set(HumanBodyBones.Neck, HumanBodyBones.Head);
            foreach (var side in new[] { "Left", "Right" })
            {
                HumanBodyBones B(string name) => (HumanBodyBones)Enum.Parse(typeof(HumanBodyBones), side + name);
                Set(B("Shoulder"), B("UpperArm"));
                Set(B("UpperArm"), B("LowerArm"));
                Set(B("LowerArm"), B("Hand"));
                Set(B("Hand"), B("MiddleProximal"), B("IndexProximal"), B("RingProximal"));
                Set(B("UpperLeg"), B("LowerLeg"));
                Set(B("LowerLeg"), B("Foot"));
                Set(B("Foot"), B("Toes"));
                foreach (var finger in new[] { "Thumb", "Index", "Middle", "Ring", "Little" })
                {
                    Set(B(finger + "Proximal"), B(finger + "Intermediate"), B(finger + "Distal"));
                    Set(B(finger + "Intermediate"), B(finger + "Distal"));
                }
            }
            return map;
        }
    }
}
