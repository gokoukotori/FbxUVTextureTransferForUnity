namespace GokouKotori.FBXUVTextureTransfer.Editor
{
    // The manual-correction wording shared by the Eye and Body pin editors. Runtime code does not use it.
    internal static class FBXUVManualCorrectionLabels
    {
        internal const string Enable = "手補正を有効にする";
        internal const string ResetAll = "手補正をすべてリセット";
        internal const string Compare = "押している間は手補正前";
        internal const string Outline = "輪郭";
        internal const string ResetMove = "移動を戻す";
        internal const string DeletePin = "ピンを削除";

        internal const string PinsLockedMessage = "手補正が無効なため、ピンを追加できません。追加するには「手補正を有効にする」を入れてください。";

        internal static readonly string[] Tools = { "移動", "補正ピンを追加", "固定ピンを追加" };
    }
}
