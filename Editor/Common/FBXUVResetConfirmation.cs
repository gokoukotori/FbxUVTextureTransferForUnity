using System;
using UnityEditor;

namespace GokouKotori.FBXUVTextureTransfer.Editor
{
    // The confirmation shared by every "reset all pins" button. Tests replace Dialog and restore it afterwards.
    internal static class FBXUVResetConfirmation
    {
        internal const string Title = "すべてリセット";

        internal static Func<string, string, bool> Dialog = (title, message)
            => EditorUtility.DisplayDialog(title, message, "リセット", "キャンセル");

        internal static string Message(int pinCount) => $"すべてのピン（{pinCount}個）を削除します。よろしいですか？";

        internal static bool Confirm(int pinCount) => Dialog(Title, Message(pinCount));
    }
}
