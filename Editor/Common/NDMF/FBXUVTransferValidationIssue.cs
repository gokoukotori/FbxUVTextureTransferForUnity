using Object = UnityEngine.Object;

namespace GokouKotori.FBXUVTextureTransfer.Editor.NDMF
{
    // One validation error and the object it is reported on (the inspector shows it, NDMF selects it).
    internal readonly struct FBXUVTransferValidationIssue
    {
        internal FBXUVTransferValidationIssue(string message, Object contextObject)
        {
            Message = message;
            ContextObject = contextObject;
        }

        internal string Message { get; }
        internal Object ContextObject { get; }
    }
}
