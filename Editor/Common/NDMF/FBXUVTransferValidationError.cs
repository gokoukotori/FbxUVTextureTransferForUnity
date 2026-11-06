using System;
using System.Collections.Generic;
using nadena.dev.ndmf;
using nadena.dev.ndmf.localization;

namespace GokouKotori.FBXUVTextureTransfer.Editor.NDMF
{
    internal sealed class FBXUVTransferValidationError : SimpleError
    {
        private const string Title = "FBX UV Texture Transfer の設定が不正です";
        private readonly string message;

        private static readonly Localizer ErrorLocalizer = new Localizer(
            "ja-JP",
            () => new List<(string, Func<string, string>)>
            {
                ("ja-JP", Lookup),
                ("en-US", Lookup)
            });

        internal FBXUVTransferValidationError(string message)
        {
            this.message = message;
        }

        public override Localizer Localizer => ErrorLocalizer;
        public override string TitleKey => "FBXUVTransfer:Validation:Title";
        public override string DetailsKey => "FBXUVTransfer:Validation:Details";
        public override string HintKey => "FBXUVTransfer:Validation:Hint";
        public override string[] DetailsSubst => new[] { message };
        public override ErrorSeverity Severity => ErrorSeverity.Error;

        private static string Lookup(string key)
        {
            switch (key)
            {
                case "FBXUVTransfer:Validation:Title":
                    return Title;
                case "FBXUVTransfer:Validation:Details":
                    return "{0}";
                case "FBXUVTransfer:Validation:Hint":
                    return "対象コンポーネントを選択し、Inspector または FBX UV Texture Transfer ウィンドウで設定を修正してください。";
                default:
                    return null;
            }
        }
    }
}
