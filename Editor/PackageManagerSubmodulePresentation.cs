using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace MartinCalander.GitSubmoduleManager.Editor
{
    internal sealed class PackageManagerSubmoduleInfo
    {
        internal const string TagLabel = "Submodule";
        internal const string GitHubSourceLabel = "GitHub";
        internal const string GitSourceLabel = "Git";

        internal PackageManagerSubmoduleInfo(
            string packageName,
            string packagePath,
            string fullPackagePath,
            string repositoryUrl,
            bool isGitHub)
        {
            PackageName = packageName ?? string.Empty;
            PackagePath = packagePath ?? string.Empty;
            FullPackagePath = fullPackagePath ?? string.Empty;
            RepositoryUrl = repositoryUrl ?? string.Empty;
            IsGitHub = isGitHub;
        }

        internal string PackageName { get; }
        internal string PackagePath { get; }
        internal string FullPackagePath { get; }
        internal string RepositoryUrl { get; }
        internal bool IsGitHub { get; }
        internal string SourceLabel => IsGitHub ? GitHubSourceLabel : GitSourceLabel;
        internal string SourceTooltip => GitUtility.FormatRepositoryUrlForDisplay(RepositoryUrl);
    }

    internal sealed class PackageManagerSubmoduleSnapshotData
    {
        private static readonly StringComparer PathComparer =
            Path.DirectorySeparatorChar == '\\'
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal;

        private readonly Dictionary<string, PackageManagerSubmoduleInfo> byName;
        private readonly Dictionary<string, PackageManagerSubmoduleInfo> byFullPath;
        private readonly HashSet<string> githubRepositories;

        private PackageManagerSubmoduleSnapshotData(
            Dictionary<string, PackageManagerSubmoduleInfo> byName,
            Dictionary<string, PackageManagerSubmoduleInfo> byFullPath,
            HashSet<string> githubRepositories)
        {
            this.byName = byName;
            this.byFullPath = byFullPath;
            this.githubRepositories = githubRepositories;
        }

        internal static PackageManagerSubmoduleSnapshotData Empty { get; } =
            new PackageManagerSubmoduleSnapshotData(
                new Dictionary<string, PackageManagerSubmoduleInfo>(StringComparer.Ordinal),
                new Dictionary<string, PackageManagerSubmoduleInfo>(PathComparer),
                new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        internal int Count => byName.Count;

        internal static PackageManagerSubmoduleSnapshotData Create(
            IEnumerable<GitPackageInfo> packages,
            string projectRoot)
        {
            var nameMap = new Dictionary<string, PackageManagerSubmoduleInfo>(StringComparer.Ordinal);
            var pathMap = new Dictionary<string, PackageManagerSubmoduleInfo>(PathComparer);
            var repositorySet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (packages == null || string.IsNullOrWhiteSpace(projectRoot))
            {
                return new PackageManagerSubmoduleSnapshotData(
                    nameMap,
                    pathMap,
                    repositorySet);
            }

            foreach (GitPackageInfo package in packages)
            {
                if (package == null || !GitUtility.IsPackagePath(package.Path))
                    continue;

                string packageName = !string.IsNullOrWhiteSpace(package.PackageName)
                    ? package.PackageName.Trim()
                    : Path.GetFileName(GitUtility.NormalizePath(package.Path));
                if (!GitUtility.IsValidUpmPackageName(packageName))
                    continue;

                string normalizedPackagePath = GitUtility.NormalizePath(package.Path);
                string fullPackagePath = NormalizeFullPath(
                    Path.Combine(projectRoot, normalizedPackagePath));
                string repositoryUrl = package.Url?.Trim() ?? string.Empty;
                bool isGitHub = GitHubUtility.TryParseGitHubRepo(
                    repositoryUrl,
                    out string repositoryOwner,
                    out string repositoryName);
                var info = new PackageManagerSubmoduleInfo(
                    packageName,
                    normalizedPackagePath,
                    fullPackagePath,
                    repositoryUrl,
                    isGitHub);

                nameMap[packageName] = info;
                if (!string.IsNullOrEmpty(fullPackagePath))
                    pathMap[fullPackagePath] = info;
                if (isGitHub)
                    repositorySet.Add(BuildRepositoryIdentity(repositoryOwner, repositoryName));
            }

            return new PackageManagerSubmoduleSnapshotData(
                nameMap,
                pathMap,
                repositorySet);
        }

        internal bool ContainsGitHubRepository(string owner, string repository)
        {
            string identity = BuildRepositoryIdentity(owner, repository);
            return !string.IsNullOrEmpty(identity) &&
                   githubRepositories.Contains(identity);
        }

        private static string BuildRepositoryIdentity(string owner, string repository)
        {
            if (string.IsNullOrWhiteSpace(owner) ||
                string.IsNullOrWhiteSpace(repository))
            {
                return string.Empty;
            }

            return owner.Trim() + "/" + repository.Trim();
        }

        internal bool TryGet(
            string packageName,
            string localPath,
            bool isInstalled,
            out PackageManagerSubmoduleInfo info)
        {
            info = null;
            if (!isInstalled)
                return false;

            string normalizedLocalPath = NormalizeFullPath(localPath);
            if (!string.IsNullOrEmpty(normalizedLocalPath))
            {
                if (byFullPath.TryGetValue(normalizedLocalPath, out info))
                    return true;

                // A concrete path that points somewhere else must not be classified
                // by name as the configured package submodule.
                return false;
            }

            return !string.IsNullOrWhiteSpace(packageName) &&
                   byName.TryGetValue(packageName.Trim(), out info);
        }

        internal static string NormalizeFullPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return string.Empty;

            try
            {
                return Path.GetFullPath(path)
                    .Replace('\\', '/')
                    .TrimEnd('/');
            }
            catch
            {
                return string.Empty;
            }
        }
    }

    internal static class PackageManagerSubmodulePresentation
    {
        internal const string InformationCardIconClassName = "informationCardIcon";
        internal const string InformationCardTextClassName = "informationCardText";
        internal const string CustomTagClassName =
            "git-submodule-manager-tag";
        internal const string RepositoryVisibilityTagClassName =
            "git-submodule-manager-repository-visibility-tag";
        internal const string CustomTagContainerClassName =
            "git-submodule-manager-tag-container";
        internal const string CustomSourceIconClassName =
            "git-submodule-manager-source-icon";
        internal const string NativeDisableEllipsisClassName = "disable-ellipsis";
        internal const string NativeTagContainerName = "tagContainer";
        internal const string TagTooltip = "Installed as a Git submodule under Packages/.";
        internal const string PublicRepositoryTagLabel = "Public";
        internal const string PrivateRepositoryTagLabel = "Private";
        internal const string PublicRepositoryTagTooltip =
            "This GitHub repository is public.";
        internal const string PrivateRepositoryTagTooltip =
            "This GitHub repository is private and requires access.";

        private const BindingFlags AnyInstance =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        internal static bool TryGetVersionIdentity(
            object packageVersion,
            out string packageName,
            out string localPath,
            out bool isInstalled)
        {
            packageName = string.Empty;
            localPath = string.Empty;
            isInstalled = false;
            if (packageVersion == null)
                return false;

            packageName = ReadStringProperty(packageVersion, "name");
            localPath = ReadStringProperty(packageVersion, "localPath");

            PropertyInfo installedProperty = packageVersion.GetType().GetProperty(
                "isInstalled",
                AnyInstance);
            if (installedProperty != null && installedProperty.PropertyType == typeof(bool))
            {
                try
                {
                    isInstalled = (bool)installedProperty.GetValue(packageVersion, null);
                }
                catch
                {
                    return false;
                }
            }
            else
            {
                // All supported Package Manager IPackageVersion contracts expose
                // isInstalled. Fallback only helps test doubles and future versions
                // that retain a concrete installed local path.
                isInstalled = !string.IsNullOrWhiteSpace(localPath);
            }

            return isInstalled &&
                   (!string.IsNullOrWhiteSpace(packageName) ||
                    !string.IsNullOrWhiteSpace(localPath));
        }

        internal static bool TryGetPresentation(
            object packageVersion,
            out PackageManagerSubmoduleInfo info)
        {
            info = null;
            return TryGetVersionIdentity(
                       packageVersion,
                       out string packageName,
                       out string localPath,
                       out bool isInstalled) &&
                   PackageManagerSubmoduleSnapshot.TryGet(
                       packageName,
                       localPath,
                       isInstalled,
                       out info);
        }

        internal static bool ApplyTagLabel(
            object tagElement,
            PackageManagerSubmoduleInfo info)
        {
            if (info == null || !(tagElement is TextElement textElement))
                return false;

            textElement.text = PackageManagerSubmoduleInfo.TagLabel;
            textElement.tooltip = TagTooltip;
            textElement.AddToClassList(CustomTagClassName);
            textElement.AddToClassList(NativeDisableEllipsisClassName);
            ApplyTagContainerOverride(textElement.parent);
            return true;
        }

        internal static void ResetCustomTagLabel(object tagElement)
        {
            if (!(tagElement is TextElement textElement) ||
                !textElement.ClassListContains(CustomTagClassName))
            {
                return;
            }

            // PackageDynamicTagLabel caches its previous PackageTag and skips
            // rebuilding when a recycled row remains InDevelopment. Restore
            // only values that are still ours; if Unity changed the tag during
            // its Refresh, its newly assigned text and tooltip stay authoritative.
            bool stillUsesCustomText = string.Equals(
                    textElement.text,
                    PackageManagerSubmoduleInfo.TagLabel,
                    StringComparison.Ordinal);
            if (stillUsesCustomText)
            {
                textElement.text = L10n.Tr("Custom");
                // Unity adds this class itself for short built-in tags such as
                // Git and Exp. Remove it only when the label still contains our
                // stale Submodule presentation; otherwise the refreshed tag owns it.
                textElement.RemoveFromClassList(NativeDisableEllipsisClassName);
            }

            if (string.Equals(textElement.tooltip, TagTooltip, StringComparison.Ordinal))
                textElement.tooltip = string.Empty;

            ResetTagContainerOverride(textElement.parent);
            textElement.RemoveFromClassList(CustomTagClassName);
        }

        internal static bool ApplyRepositoryVisibilityTag(
            object tagElement,
            bool isPrivate)
        {
            if (!(tagElement is TextElement textElement))
                return false;

            textElement.text = L10n.Tr(
                isPrivate
                    ? PrivateRepositoryTagLabel
                    : PublicRepositoryTagLabel);
            textElement.tooltip = isPrivate
                ? PrivateRepositoryTagTooltip
                : PublicRepositoryTagTooltip;
            textElement.AddToClassList(RepositoryVisibilityTagClassName);
            textElement.AddToClassList(NativeDisableEllipsisClassName);
            return true;
        }

        internal static void ResetRepositoryVisibilityTag(object tagElement)
        {
            if (!(tagElement is TextElement textElement) ||
                !textElement.ClassListContains(RepositoryVisibilityTagClassName))
            {
                return;
            }

            bool stillUsesVisibilityText =
                string.Equals(
                    textElement.text,
                    L10n.Tr(PublicRepositoryTagLabel),
                    StringComparison.Ordinal) ||
                string.Equals(
                    textElement.text,
                    L10n.Tr(PrivateRepositoryTagLabel),
                    StringComparison.Ordinal);
            if (stillUsesVisibilityText)
            {
                // Projected packages use Unity's native Git tag. Restore that
                // baseline when the same label instance is recycled and Unity
                // skips rebuilding because the PackageTag itself did not change.
                textElement.text = L10n.Tr("Git");
            }

            if (string.Equals(
                    textElement.tooltip,
                    PublicRepositoryTagTooltip,
                    StringComparison.Ordinal) ||
                string.Equals(
                    textElement.tooltip,
                    PrivateRepositoryTagTooltip,
                    StringComparison.Ordinal))
            {
                textElement.tooltip = string.Empty;
            }

            // The native Git presentation owns disable-ellipsis. Preserve it
            // both for the restored Git label and for any label Unity refreshed.
            textElement.RemoveFromClassList(RepositoryVisibilityTagClassName);
        }

        private static void ApplyTagContainerOverride(VisualElement container)
        {
            if (container == null ||
                container.name != NativeTagContainerName ||
                container.style.maxWidth.keyword != StyleKeyword.Null)
            {
                return;
            }

            // Unity caps this list-row container at 70 px, which is slightly
            // narrower than Submodule plus the native padding, border and margin.
            // Relax only this row and mark the inline override for exact cleanup.
            container.style.maxWidth = StyleKeyword.None;
            container.AddToClassList(CustomTagContainerClassName);
        }

        private static void ResetTagContainerOverride(VisualElement container)
        {
            if (container == null ||
                !container.ClassListContains(CustomTagContainerClassName))
            {
                return;
            }

            if (container.style.maxWidth.keyword == StyleKeyword.None)
                container.style.maxWidth = StyleKeyword.Null;

            container.RemoveFromClassList(CustomTagContainerClassName);
        }

        internal static bool ApplySourceCard(
            object sourceCard,
            PackageManagerSubmoduleInfo info,
            Texture2D gitIcon)
        {
            if (info == null || !(sourceCard is VisualElement card))
                return false;

            TextElement content = card.Q<TextElement>(className: InformationCardTextClassName);
            if (content == null)
                return false;

            content.text = info.SourceLabel;
            content.tooltip = info.SourceTooltip;
            // Placeholder discovery versions have no UpmCache PackageInfo, so
            // Unity hides the Source card before our postfix runs. Make only a
            // recognized GitHub/submodule card visible again; the next native
            // Refresh remains authoritative for every other selection.
            card.style.display = DisplayStyle.Flex;

            VisualElement iconElement = card.Q<VisualElement>(
                className: InformationCardIconClassName);
            if (iconElement != null)
            {
                ResetCustomSourceIcon(card);
                if (info.IsGitHub && gitIcon != null)
                {
                    iconElement.style.backgroundImage = new StyleBackground(gitIcon);
                    iconElement.style.display = DisplayStyle.Flex;
                    iconElement.tooltip = GitHubSourceLabelTooltip;
                    iconElement.AddToClassList(CustomSourceIconClassName);
                }
            }

            return true;
        }

        internal static bool ApplyTechnicalNameCard(
            object technicalNameCard,
            string packageName)
        {
            if (string.IsNullOrWhiteSpace(packageName) ||
                !(technicalNameCard is VisualElement card))
            {
                return false;
            }

            TextElement content = card.Q<TextElement>(
                className: InformationCardTextClassName);
            if (content == null)
                return false;

            string normalizedName = packageName.Trim();
            content.text = normalizedName;
            content.tooltip = string.Empty;
            card.style.display = DisplayStyle.Flex;

            object copyIcon = GetFieldValue(card, "m_CopyIcon");
            MethodInfo setTextToCopy = copyIcon?.GetType().GetMethod(
                "SetTextToCopy",
                AnyInstance,
                null,
                new[] { typeof(string) },
                null);
            setTextToCopy?.Invoke(copyIcon, new object[] { normalizedName });
            return true;
        }

        internal static bool ApplyAuthorLabel(object authorLabel, string owner)
        {
            if (string.IsNullOrWhiteSpace(owner) ||
                !(authorLabel is VisualElement container))
            {
                return false;
            }

            container.Clear();
            container.Add(new Label(L10n.Tr("By")));
            container.Add(new Label(owner.Trim()) { name = "authorLabel" });
            return true;
        }

        internal static void ResetCustomSourceIcon(object sourceCard)
        {
            if (!(sourceCard is VisualElement card))
                return;

            VisualElement iconElement = card.Q<VisualElement>(
                className: InformationCardIconClassName);
            if (iconElement == null)
                return;

            // Cards never decorated by this package remain exact no-ops. The
            // marker lets a recycled SourceInfoCard restore Unity's own icon
            // after the selection moves away from a GitHub submodule.
            if (!iconElement.ClassListContains(CustomSourceIconClassName))
                return;

            iconElement.style.backgroundImage = default(StyleBackground);
            if (string.Equals(
                    iconElement.tooltip,
                    GitHubSourceLabelTooltip,
                    StringComparison.Ordinal))
            {
                iconElement.tooltip = string.Empty;
            }
            iconElement.RemoveFromClassList(CustomSourceIconClassName);
        }

        private const string GitHubSourceLabelTooltip =
            "This package repository is hosted on GitHub.";

        private static object GetFieldValue(object instance, string fieldName)
        {
            if (instance == null)
                return null;

            for (Type type = instance.GetType(); type != null; type = type.BaseType)
            {
                FieldInfo field = type.GetField(fieldName, AnyInstance);
                if (field != null)
                    return field.GetValue(instance);
            }

            return null;
        }

        private static string ReadStringProperty(object instance, string propertyName)
        {
            try
            {
                PropertyInfo property = instance.GetType().GetProperty(
                    propertyName,
                    AnyInstance);
                return property?.PropertyType == typeof(string)
                    ? (string)property.GetValue(instance, null) ?? string.Empty
                    : string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
