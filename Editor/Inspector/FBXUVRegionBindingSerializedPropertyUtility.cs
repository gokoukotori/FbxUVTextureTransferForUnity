using System;
using UnityEditor;

namespace GokouKotori.FBXUVTextureTransfer.Editor
{
    internal static class FBXUVRegionBindingSerializedPropertyUtility
    {
        private const string BindingsPropertyName = "regionBindings";

        internal static SerializedProperty FindBindings(SerializedObject serializedLayer)
        {
            return serializedLayer?.FindProperty(BindingsPropertyName);
        }

        internal static int Add(SerializedProperty bindings)
        {
            if (bindings == null || !bindings.isArray) return -1;

            var name = GenerateUniqueName(bindings);
            var index = bindings.arraySize;
            bindings.arraySize++;

            var binding = bindings.GetArrayElementAtIndex(index);
            binding.FindPropertyRelative("name").stringValue = name;
            binding.FindPropertyRelative("enabled").boolValue = true;
            binding.FindPropertyRelative("orientation").intValue =
                (int)FBXUVTransferOrientation.Preserve;
            InitializeRegion(binding.FindPropertyRelative("sourceRegion"));
            InitializeRegion(binding.FindPropertyRelative("targetRegion"));
            return index;
        }

        internal static bool TryRename(
            SerializedProperty bindings,
            int index,
            string requestedName,
            out string error)
        {
            if (!TryGetElement(bindings, index, out var binding))
            {
                error = "変更対象のRegionが見つかりません。";
                return false;
            }

            var normalizedName = (requestedName ?? string.Empty).Trim();
            if (normalizedName.Length == 0)
            {
                error = "Region名を空にはできません。";
                return false;
            }

            for (var candidateIndex = 0; candidateIndex < bindings.arraySize; candidateIndex++)
            {
                if (candidateIndex == index) continue;
                var candidate = bindings
                    .GetArrayElementAtIndex(candidateIndex)
                    .FindPropertyRelative("name")
                    .stringValue;
                if (NamesEqual(candidate, normalizedName))
                {
                    error = "同名のRegionが既にあります。";
                    return false;
                }
            }

            binding.FindPropertyRelative("name").stringValue = normalizedName;
            error = string.Empty;
            return true;
        }

        internal static bool DeleteAt(SerializedProperty bindings, int index)
        {
            if (!TryGetElement(bindings, index, out _)) return false;
            bindings.DeleteArrayElementAtIndex(index);
            return true;
        }

        internal static bool Move(SerializedProperty bindings, int oldIndex, int newIndex)
        {
            if (!TryGetElement(bindings, oldIndex, out _) ||
                newIndex < 0 ||
                newIndex >= bindings.arraySize ||
                oldIndex == newIndex)
            {
                return false;
            }

            return bindings.MoveArrayElement(oldIndex, newIndex);
        }

        internal static bool ApplyModifiedProperties(SerializedObject serializedLayer)
        {
            if (serializedLayer == null || !serializedLayer.ApplyModifiedProperties()) return false;

            foreach (var changedTarget in serializedLayer.targetObjects)
            {
                PrefabUtility.RecordPrefabInstancePropertyModifications(changedTarget);
                EditorUtility.SetDirty(changedTarget);
            }

            return true;
        }

        internal static string GenerateUniqueName(SerializedProperty bindings)
        {
            const string baseName = "Region";
            if (!ContainsName(bindings, baseName)) return baseName;

            for (var suffix = 2; suffix < int.MaxValue; suffix++)
            {
                var candidate = baseName + " " + suffix;
                if (!ContainsName(bindings, candidate)) return candidate;
            }

            throw new InvalidOperationException("新しいRegion名を生成できませんでした。");
        }

        private static bool ContainsName(SerializedProperty bindings, string name)
        {
            if (bindings == null || !bindings.isArray) return false;
            for (var index = 0; index < bindings.arraySize; index++)
            {
                var candidate = bindings
                    .GetArrayElementAtIndex(index)
                    .FindPropertyRelative("name")
                    .stringValue;
                if (NamesEqual(candidate, name)) return true;
            }

            return false;
        }

        private static bool NamesEqual(string left, string right)
        {
            return string.Equals(
                (left ?? string.Empty).Trim(),
                (right ?? string.Empty).Trim(),
                StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryGetElement(
            SerializedProperty bindings,
            int index,
            out SerializedProperty element)
        {
            element = null;
            if (bindings == null || !bindings.isArray || index < 0 || index >= bindings.arraySize)
            {
                return false;
            }

            element = bindings.GetArrayElementAtIndex(index);
            return true;
        }

        private static void InitializeRegion(SerializedProperty region)
        {
            if (region == null) return;

            region.FindPropertyRelative("mesh").objectReferenceValue = null;
            region.FindPropertyRelative("subMeshIndex").intValue = 0;
            region.FindPropertyRelative("uvChannel").intValue = 0;
            region.FindPropertyRelative("islandId").intValue = -1;
            region.FindPropertyRelative("triangles").arraySize = 0;
            region.FindPropertyRelative("meshHash").stringValue = string.Empty;

            var bounds = region.FindPropertyRelative("bounds");
            bounds.FindPropertyRelative("minU").floatValue = 0f;
            bounds.FindPropertyRelative("minV").floatValue = 0f;
            bounds.FindPropertyRelative("maxU").floatValue = 0f;
            bounds.FindPropertyRelative("maxV").floatValue = 0f;
        }
    }
}
