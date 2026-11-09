namespace GokouKotori.FBXUVTextureTransfer.Editor
{
    // Single source of the editor window names, shared by window titles, Tools menu items and Inspector open buttons.
    internal static class FBXUVEditorNames
    {
        internal const string MenuRoot = "Tools/Gokoukotori/FBX UV Texture Transfer/";
        internal const string TextureTransferEditor = "テクスチャ転送エディター";
        internal const string MakeupTransferEditor = "メイク転送エディター";
        internal const string EyeTransferEditor = "アイ転送エディター";
        internal const string BodyTransferEditor = "ボディ転送エディター";

        internal static string OpenButton(string editorName) => editorName + "を開く";
    }
}
