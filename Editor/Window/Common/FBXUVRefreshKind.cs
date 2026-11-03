namespace GokouKotori.FBXUVTextureTransfer.Editor
{
    // Larger values win when several refresh requests are merged.
    internal enum FBXUVRefreshKind
    {
        None = 0,
        PreservePending = 1,
        RestoreSaved = 2
    }
}
