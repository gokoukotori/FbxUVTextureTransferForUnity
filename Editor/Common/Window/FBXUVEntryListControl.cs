using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace GokouKotori.FBXUVTextureTransfer.Editor
{
    internal enum FBXUVEntryCommand
    {
        None,
        Select,
        Add,
        Remove,
        MoveUp,
        MoveDown
    }

    // Shared add / select / remove row and enabled / name row for the entries a window edits (Regions, eyes).
    // The control only draws and asks; the window applies the returned command to its own data.
    internal sealed class FBXUVEntryListControl
    {
        internal delegate bool RenameHandler(int index, string requestedName, out string error);

        private static readonly GUIContent MoveUpLabel = new GUIContent("上へ", "一覧の順番を1つ上げます。");
        private static readonly GUIContent MoveDownLabel = new GUIContent("下へ", "一覧の順番を1つ下げます。");

        private readonly string kind;
        private readonly string removeDetail;
        private int nameErrorIndex = -1;
        private string nameError;

        // Replaced by tests; the default asks with a modal dialog.
        internal Func<string, string, bool> Confirm = (title, message) =>
            EditorUtility.DisplayDialog(title, message, "削除", "キャンセル");

        internal FBXUVEntryListControl(string kind, string removeDetail)
        {
            this.kind = kind;
            this.removeDetail = removeDetail;
        }

        internal string NameError(int index) => index == nameErrorIndex ? nameError : null;

        internal void ClearNameError()
        {
            nameErrorIndex = -1;
            nameError = null;
        }

        // Draws [編集する{kind}] [{kind}を追加] [この{kind}を削除] [上へ] [下へ]. Select writes the new index into selected;
        // Remove is returned only after the user confirmed it. The moves are offered only where the entry can move.
        internal FBXUVEntryCommand DrawSelector(string[] labels, ref int selected)
        {
            var command = FBXUVEntryCommand.None;
            var hasEntries = labels.Length > 0;
            using (new EditorGUILayout.HorizontalScope())
            {
                if (hasEntries)
                {
                    var next = EditorGUILayout.Popup($"編集する{kind}", Mathf.Clamp(selected, 0, labels.Length - 1), labels);
                    if (next != selected)
                    {
                        selected = next;
                        command = FBXUVEntryCommand.Select;
                    }
                }
                else
                {
                    EditorGUILayout.PrefixLabel($"編集する{kind}");
                    GUILayout.FlexibleSpace();
                }

                if (GUILayout.Button($"{kind}を追加", GUILayout.ExpandWidth(false))) command = FBXUVEntryCommand.Add;
                using (new EditorGUI.DisabledScope(!hasEntries))
                {
                    if (GUILayout.Button($"この{kind}を削除", GUILayout.ExpandWidth(false)) &&
                        ConfirmRemove(labels[Mathf.Clamp(selected, 0, labels.Length - 1)]))
                    {
                        command = FBXUVEntryCommand.Remove;
                    }
                }

                using (new EditorGUI.DisabledScope(!hasEntries || selected <= 0))
                {
                    if (GUILayout.Button(MoveUpLabel, GUILayout.ExpandWidth(false))) command = FBXUVEntryCommand.MoveUp;
                }

                using (new EditorGUI.DisabledScope(!hasEntries || selected >= labels.Length - 1))
                {
                    if (GUILayout.Button(MoveDownLabel, GUILayout.ExpandWidth(false))) command = FBXUVEntryCommand.MoveDown;
                }
            }

            if (!hasEntries)
            {
                EditorGUILayout.HelpBox($"{kind}がありません。「{kind}を追加」で追加してください。", MessageType.Info);
            }

            if (command != FBXUVEntryCommand.None) ClearNameError();
            return command;
        }

        // Draws the enabled toggle and the name field of one serialized entry ("enabled" / "name").
        // The toggle is written to the property; the caller applies the SerializedObject. Renames go through tryRename.
        internal void DrawEnabledAndName(SerializedProperty entry, int index, RenameHandler tryRename)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                var enabled = entry.FindPropertyRelative("enabled");
                EditorGUI.BeginChangeCheck();
                var nextEnabled = EditorGUILayout.ToggleLeft("有効", enabled.boolValue, GUILayout.Width(60f));
                if (EditorGUI.EndChangeCheck()) enabled.boolValue = nextEnabled;

                var current = entry.FindPropertyRelative("name").stringValue;
                var requested = EditorGUILayout.DelayedTextField("名前", current);
                if (!string.Equals(requested, current, StringComparison.Ordinal)) Rename(index, requested, tryRename);
            }

            var error = NameError(index);
            if (error != null) EditorGUILayout.HelpBox(error, MessageType.Error);
        }

        internal bool Rename(int index, string requestedName, RenameHandler tryRename)
        {
            if (tryRename(index, requestedName, out var error))
            {
                ClearNameError();
                return true;
            }

            nameErrorIndex = index;
            nameError = error;
            return false;
        }

        internal bool ConfirmRemove(string name)
        {
            return Confirm($"{kind}を削除", $"「{name}」を削除しますか？\n{removeDetail}");
        }

        // The layers' name rule: names are trimmed and compared case-insensitively, and null counts as empty.
        internal static bool NamesEqual(string left, string right) =>
            string.Equals((left ?? string.Empty).Trim(), (right ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase);

        // Names follow NamesEqual; the entry's own current name is not a duplicate.
        internal static bool ValidateName(
            string kind,
            IReadOnlyList<string> names,
            int index,
            string requestedName,
            out string normalized,
            out string error)
        {
            normalized = (requestedName ?? string.Empty).Trim();
            if (normalized.Length == 0)
            {
                error = $"{kind}の名前を空にはできません。";
                return false;
            }

            for (var other = 0; other < names.Count; other++)
            {
                if (other == index) continue;
                if (NamesEqual(names[other], normalized))
                {
                    error = $"同じ名前の{kind}が既にあります。";
                    return false;
                }
            }

            error = string.Empty;
            return true;
        }
    }
}
