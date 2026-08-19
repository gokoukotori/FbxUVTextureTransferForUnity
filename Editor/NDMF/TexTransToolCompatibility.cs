using System;
using System.Collections.Generic;
using System.Reflection;
using net.rs64.TexTransTool.MultiLayerImage;
using UnityEditor.PackageManager;

namespace GokouKotori.FBXUVTextureTransfer.Editor.NDMF
{
    internal readonly struct TexTransToolCompatibilityStatus
    {
        public TexTransToolCompatibilityStatus(string version, bool isUnityBackend)
        {
            Version = version;
            IsUnityBackend = isUnityBackend;
        }

        public string Version { get; }
        public bool IsUnityBackend { get; }
    }

    /// <summary>
    /// TexTransTool の内部設定への依存をこのクラスだけに閉じ込める。
    /// Experimental API の形が変わった場合は「取得不能」として安全側に倒す。
    /// </summary>
    internal static class TexTransToolCompatibility
    {
        internal const string RequiredPackageName = "net.rs64.tex-trans-tool";
        internal const string MinimumVersion = "1.1.0-beta.9";

        private const string ProjectConfigTypeName =
            "net.rs64.TexTransTool.TTTProjectConfig, net.rs64.tex-trans-tool.editor";

        internal static bool TryIsVersionAtLeastMinimum(
            string version,
            out bool isAtLeastMinimum,
            out string failure)
        {
            isAtLeastMinimum = false;
            failure = null;

            if (!SemanticVersion.TryParse(version, out var installedVersion))
            {
                failure = $"TexTransTool のバージョン '{version ?? "(null)"}' を SemVer 2.0 として解析できません。";
                return false;
            }

            if (!SemanticVersion.TryParse(MinimumVersion, out var minimumVersion))
            {
                failure = $"内部の TexTransTool 最低バージョン '{MinimumVersion}' を SemVer 2.0 として解析できません。";
                return false;
            }

            isAtLeastMinimum = installedVersion.CompareTo(minimumVersion) >= 0;
            return true;
        }

        internal static bool TryGetStatus(out TexTransToolCompatibilityStatus status, out string failure)
        {
            status = default;
            failure = null;

            string installedVersion;
            try
            {
                var package = PackageInfo.FindForAssembly(typeof(ExternalToolAsLayer).Assembly);
                if (package == null)
                {
                    failure = "TexTransTool パッケージを Package Manager から確認できません。";
                    return false;
                }

                if (!string.Equals(package.name, RequiredPackageName, StringComparison.Ordinal))
                {
                    failure = $"TexTransTool の実装 assembly が想定外のパッケージ '{package.name}' から読み込まれています。";
                    return false;
                }

                installedVersion = package.version;
            }
            catch (Exception exception)
            {
                failure = $"TexTransTool のバージョン取得に失敗しました: {exception.Message}";
                return false;
            }

            if (!TryIsVersionAtLeastMinimum(installedVersion, out var isAtLeastMinimum, out failure))
            {
                return false;
            }

            if (!isAtLeastMinimum)
            {
                failure = $"TexTransTool {MinimumVersion} 以上が必要です。現在は {installedVersion} です。";
                return false;
            }

            var configType = Type.GetType(ProjectConfigTypeName, false);
            if (configType == null)
            {
                failure = "TexTransTool の必要な設定型を取得できません。Experimental API が変更された可能性があります。";
                return false;
            }

            try
            {
                var instanceProperty = configType.GetProperty(
                    "instance",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.FlattenHierarchy);
                var backendProperty = configType.GetProperty(
                    "TexTransCoreEngineBackend",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                if (instanceProperty == null || backendProperty == null)
                {
                    failure = "TexTransTool の backend 設定を取得できません。Experimental API が変更された可能性があります。";
                    return false;
                }

                var config = instanceProperty.GetValue(null);
                var backend = backendProperty.GetValue(config);
                if (config == null || backend == null)
                {
                    failure = "TexTransTool の backend 設定値を取得できません。";
                    return false;
                }

                status = new TexTransToolCompatibilityStatus(installedVersion, Convert.ToInt32(backend) == 0);
                return true;
            }
            catch (Exception exception)
            {
                failure = $"TexTransTool の backend 設定取得に失敗しました: {exception.Message}";
                return false;
            }
        }

        private sealed class SemanticVersion : IComparable<SemanticVersion>
        {
            private readonly string major;
            private readonly string minor;
            private readonly string patch;
            private readonly IReadOnlyList<string> prereleaseIdentifiers;

            private SemanticVersion(
                string major,
                string minor,
                string patch,
                IReadOnlyList<string> prereleaseIdentifiers)
            {
                this.major = major;
                this.minor = minor;
                this.patch = patch;
                this.prereleaseIdentifiers = prereleaseIdentifiers;
            }

            public int CompareTo(SemanticVersion other)
            {
                if (other == null) return 1;

                var comparison = CompareNumericIdentifier(major, other.major);
                if (comparison != 0) return comparison;
                comparison = CompareNumericIdentifier(minor, other.minor);
                if (comparison != 0) return comparison;
                comparison = CompareNumericIdentifier(patch, other.patch);
                if (comparison != 0) return comparison;

                if (prereleaseIdentifiers.Count == 0)
                {
                    return other.prereleaseIdentifiers.Count == 0 ? 0 : 1;
                }

                if (other.prereleaseIdentifiers.Count == 0) return -1;

                var sharedLength = Math.Min(prereleaseIdentifiers.Count, other.prereleaseIdentifiers.Count);
                for (var index = 0; index < sharedLength; index++)
                {
                    comparison = ComparePrereleaseIdentifier(
                        prereleaseIdentifiers[index],
                        other.prereleaseIdentifiers[index]);
                    if (comparison != 0) return comparison;
                }

                return prereleaseIdentifiers.Count.CompareTo(other.prereleaseIdentifiers.Count);
            }

            internal static bool TryParse(string value, out SemanticVersion version)
            {
                version = null;
                if (string.IsNullOrEmpty(value)) return false;

                var buildSeparator = value.IndexOf('+');
                var precedence = buildSeparator < 0 ? value : value.Substring(0, buildSeparator);
                if (buildSeparator >= 0)
                {
                    var build = value.Substring(buildSeparator + 1);
                    if (!IsValidIdentifierList(build, false)) return false;
                }

                var prereleaseSeparator = precedence.IndexOf('-');
                var core = prereleaseSeparator < 0
                    ? precedence
                    : precedence.Substring(0, prereleaseSeparator);
                var prerelease = prereleaseSeparator < 0
                    ? null
                    : precedence.Substring(prereleaseSeparator + 1);

                var coreIdentifiers = core.Split('.');
                if (coreIdentifiers.Length != 3
                    || !IsValidNumericIdentifier(coreIdentifiers[0])
                    || !IsValidNumericIdentifier(coreIdentifiers[1])
                    || !IsValidNumericIdentifier(coreIdentifiers[2]))
                {
                    return false;
                }

                var prereleaseIdentifiers = Array.Empty<string>();
                if (prerelease != null)
                {
                    if (!IsValidIdentifierList(prerelease, true)) return false;
                    prereleaseIdentifiers = prerelease.Split('.');
                }

                version = new SemanticVersion(
                    coreIdentifiers[0],
                    coreIdentifiers[1],
                    coreIdentifiers[2],
                    prereleaseIdentifiers);
                return true;
            }

            private static int ComparePrereleaseIdentifier(string first, string second)
            {
                var firstIsNumeric = IsNumeric(first);
                var secondIsNumeric = IsNumeric(second);
                if (firstIsNumeric && secondIsNumeric) return CompareNumericIdentifier(first, second);
                if (firstIsNumeric) return -1;
                if (secondIsNumeric) return 1;
                return string.CompareOrdinal(first, second);
            }

            private static int CompareNumericIdentifier(string first, string second)
            {
                var lengthComparison = first.Length.CompareTo(second.Length);
                return lengthComparison != 0 ? lengthComparison : string.CompareOrdinal(first, second);
            }

            private static bool IsValidIdentifierList(string value, bool rejectLeadingZerosForNumeric)
            {
                if (string.IsNullOrEmpty(value)) return false;

                var identifiers = value.Split('.');
                for (var index = 0; index < identifiers.Length; index++)
                {
                    var identifier = identifiers[index];
                    if (string.IsNullOrEmpty(identifier)) return false;
                    for (var characterIndex = 0; characterIndex < identifier.Length; characterIndex++)
                    {
                        var character = identifier[characterIndex];
                        if (!IsAsciiAlphaNumeric(character) && character != '-') return false;
                    }

                    if (rejectLeadingZerosForNumeric
                        && IsNumeric(identifier)
                        && identifier.Length > 1
                        && identifier[0] == '0')
                    {
                        return false;
                    }
                }

                return true;
            }

            private static bool IsValidNumericIdentifier(string value)
            {
                return IsNumeric(value) && (value.Length == 1 || value[0] != '0');
            }

            private static bool IsNumeric(string value)
            {
                if (string.IsNullOrEmpty(value)) return false;
                for (var index = 0; index < value.Length; index++)
                {
                    if (value[index] < '0' || value[index] > '9') return false;
                }

                return true;
            }

            private static bool IsAsciiAlphaNumeric(char character)
            {
                return character >= '0' && character <= '9'
                    || character >= 'A' && character <= 'Z'
                    || character >= 'a' && character <= 'z';
            }
        }
    }
}
