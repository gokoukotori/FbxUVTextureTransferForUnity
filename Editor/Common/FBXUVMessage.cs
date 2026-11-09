using UnityEditor;

namespace GokouKotori.FBXUVTextureTransfer.Editor
{
    // One message shown in an inspector or editor window: a result or a note with its HelpBox type.
    internal readonly struct FBXUVMessage
    {
        private FBXUVMessage(string text, MessageType type)
        {
            Text = text;
            Type = type;
        }

        internal string Text { get; }
        internal MessageType Type { get; }
        internal bool IsEmpty => string.IsNullOrEmpty(Text);

        internal static FBXUVMessage Info(string text) => new FBXUVMessage(text, MessageType.Info);
        internal static FBXUVMessage Warning(string text) => new FBXUVMessage(text, MessageType.Warning);

        // Draws nothing for an empty message.
        internal void Draw()
        {
            if (!IsEmpty) EditorGUILayout.HelpBox(Text, Type);
        }
    }
}
