using System.IO;
using UnityEditor;
using UnityEditor.PackageManager;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;
using UnityEngine;
using UnityEngine.UIElements;

namespace MartinCalander.GitSubmoduleManager.Editor
{
    internal sealed class PackageStatusDetailsView : VisualElement
    {
        private const string HiddenClass = "package-source-hidden";
        private readonly Label status = Text("git-submodule-package-status");
        private readonly Label source = Text("git-submodule-source-url");
        private readonly Label revision = Text("git-submodule-git-revision");
        private readonly Label location = Text("git-submodule-package-location");
        private readonly VisualElement actions = new VisualElement();
        private readonly Button open;
        private readonly Button copyCommit;
        private readonly Button reveal;
        private PackageInfo selected;

        internal PackageStatusDetailsView()
        {
            name = "git-submodule-package-details";
            AddToClassList("package-source-details");
            var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(
                "Packages/com.martincalander.gitsubmodulemanager/Editor/PackageStatusDetails.uss");
            if (sheet != null)
                styleSheets.Add(sheet);
            Add(status);
            Add(source);
            Add(revision);
            Add(location);
            actions.AddToClassList("package-source-actions");
            Add(actions);
            open = Action("git-submodule-open-repository", "Open repository", () =>
            {
                string url = CurrentDetails().RepositoryUrl;
                if (!string.IsNullOrEmpty(url))
                    Application.OpenURL(url);
            });
            copyCommit = Action("git-submodule-copy-commit", "Copy installed commit", () =>
            {
                string commit = CurrentDetails().InstalledCommit;
                if (!string.IsNullOrEmpty(commit))
                    EditorGUIUtility.systemCopyBuffer = commit;
            });
            reveal = Action("git-submodule-reveal-folder", "Reveal package folder", () =>
            {
                string path = CurrentDetails().ResolvedPath;
                if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                    EditorUtility.RevealInFinder(path);
            });
        }

        private PackageSourceDetails CurrentDetails() => PackageManagerPackageStatus.GetSourceDetails(selected);

        internal void Refresh(PackageInfo package)
        {
            selected = package;
            Render(package == null ? string.Empty : PackageManagerPackageStatus.Describe(package),
                CurrentDetails(), package?.source == PackageSource.Git);
        }

        internal void Render(string description, PackageSourceDetails details, bool nativeGitSource)
        {
            EnableInClassList(HiddenClass, string.IsNullOrEmpty(description));
            status.text = description;
            status.tooltip = string.Empty;
            bool showSource = !nativeGitSource && !string.IsNullOrEmpty(details.SourceUrl);
            SetText(source, showSource ? "Source: " + details.SourceUrl : string.Empty);
            SetText(revision, string.IsNullOrEmpty(details.RevisionDescription) ? string.Empty :
                "Git revision: " + details.RevisionDescription);
            revision.tooltip = string.IsNullOrEmpty(details.InstalledCommit) ? string.Empty :
                "Requested revision → installed commit: " + details.InstalledCommit;
            SetText(location, string.IsNullOrEmpty(details.ResolvedPath) ? string.Empty :
                "Location: " + details.ResolvedPath);
            SetAction(open, !string.IsNullOrEmpty(details.RepositoryUrl), details.RepositoryUrl);
            SetAction(copyCommit, !string.IsNullOrEmpty(details.InstalledCommit), details.InstalledCommit);
            SetAction(reveal, !string.IsNullOrEmpty(details.ResolvedPath), details.ResolvedPath);
            actions.EnableInClassList(HiddenClass,
                string.IsNullOrEmpty(details.RepositoryUrl) &&
                string.IsNullOrEmpty(details.InstalledCommit) && string.IsNullOrEmpty(details.ResolvedPath));
        }

        private Button Action(string elementName, string text, System.Action clicked)
        {
            var button = new Button(clicked) { name = elementName, text = text };
            actions.Add(button);
            return button;
        }

        private static Label Text(string elementName)
        {
            var label = new Label { name = elementName, enableRichText = false };
            label.selection.isSelectable = true;
            label.AddToClassList("package-source-text");
            return label;
        }

        private static void SetText(Label label, string text)
        {
            label.text = text ?? string.Empty;
            label.tooltip = label.text;
            label.EnableInClassList(HiddenClass, string.IsNullOrEmpty(text));
        }

        private static void SetAction(Button button, bool available, string tooltip)
        {
            button.EnableInClassList(HiddenClass, !available);
            button.SetEnabled(available);
            button.tooltip = available ? tooltip ?? string.Empty : string.Empty;
        }
    }
}
