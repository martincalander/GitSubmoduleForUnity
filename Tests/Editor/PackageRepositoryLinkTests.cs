using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace MartinCalander.GitSubmoduleManager.Editor.Tests
{
    [Category("PackageSourceFeatures")]
    public sealed class PackageRepositoryLinkTests
    {
        private const string Name = "com.example.package";
        private const string Url = "https://github.com/example/package.git#main";
        private const string Repository = "https://github.com/example/package";
        private const string RowName = "upmLinksContainer";
        private const string SeparatorName = PackageRepositoryLink.ElementName + "-separator";
        private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly string Root = Path.Combine(Path.GetTempPath(), "repository-link-tests");
        private static readonly string CachePath = Path.Combine(Root, "Library", "PackageCache", Name);

        [Test]
        public void RepositoryPrecedesNativeLinksWithoutReplacingTheirRowOrOrder()
        {
            var links = new VisualElement();
            var row = new VisualElement { name = RowName };
            var documentation = new Button { text = "Documentation" };
            var separator = new Label("|");
            var changelog = new Button { text = "Changelog" };
            var licenses = new Button { text = "Licenses" };
            row.Add(documentation);
            row.Add(separator);
            row.Add(changelog);
            row.Add(licenses);
            links.Add(row);
            var otherRow = new VisualElement { name = "assetStoreLinksContainer" };
            links.Add(otherRow);

            Button repository = PackageRepositoryLink.Prepend(links, Repository, () => { });

            Assert.That(links.Q(RowName), Is.SameAs(row));
            Assert.That(row.Children().ToArray(), Is.EqualTo(new VisualElement[]
                { repository, row.Q(SeparatorName), documentation, separator, changelog, licenses }));
            Assert.That(repository.text, Is.EqualTo("Repository"));
            Assert.That(repository.tooltip, Is.EqualTo(Repository));
            Assert.That(repository.ClassListContains("link"), Is.True);
            Assert.That(row.Query(name: SeparatorName).ToList(), Has.Count.EqualTo(1));
            Assert.That(otherRow.parent, Is.SameAs(links));

            PackageRepositoryLink.Remove(links);

            Assert.That(row.Children().ToArray(), Is.EqualTo(new VisualElement[]
                { documentation, separator, changelog, licenses }));
            Assert.That(row.parent, Is.SameAs(links));
            Assert.That(otherRow.parent, Is.SameAs(links));
            Assert.That(otherRow.childCount, Is.Zero, "Unrelated empty native rows must remain intact.");
            PackageRepositoryLink.Remove(links);
            Assert.That(row.childCount, Is.EqualTo(4));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void RepositoryWithoutNativeLinksHasNoSeparatorAndRemovesItsEmptyRow(bool hasOtherRow)
        {
            var links = new VisualElement();
            var otherRow = new VisualElement { name = "assetStoreLinksContainer" };
            var native = new Button { text = "Publisher" };
            otherRow.Add(native);
            if (hasOtherRow)
                links.Add(otherRow);

            Button repository = PackageRepositoryLink.Prepend(links, Repository, () => { });

            Assert.That(repository.parent.name, Is.EqualTo(RowName));
            Assert.That(repository.parent.childCount, Is.EqualTo(1));
            Assert.That(links.Q(SeparatorName), Is.Null);
            Assert.That(links.style.display.value, Is.EqualTo(DisplayStyle.Flex));

            PackageRepositoryLink.Remove(links);

            Assert.That(links.Q(RowName), Is.Null);
            Assert.That(links.Q(PackageRepositoryLink.ElementName), Is.Null);
            if (hasOtherRow)
            {
                Assert.That(links.Children().ToArray(), Is.EqualTo(new[] { otherRow }));
                Assert.That(otherRow[0], Is.SameAs(native));
                Assert.That(links.style.display.value, Is.EqualTo(DisplayStyle.Flex));
            }
            else
                Assert.That(links.style.display.value, Is.EqualTo(DisplayStyle.None));
        }

        [TestCase(Url, Repository)]
        [TestCase("https://user:secret@github.com/example/package.git?token=secret&path=/Packages/Child#main", Repository)]
        [TestCase("git@github.com:example/package.git", Repository)]
        [TestCase("ssh://git@custom.invalid/team/package.git", "")]
        [TestCase("javascript:alert(1)", "")]
        public void CachedGitSourceSuppliesOnlyASafeRepositoryUrl(string source, string expected)
        {
            var data = Snapshot(source);
            var version = Version(source);

            Assert.That(PackageManagerPackageStatus.GetRepositoryUrl(Root, version, data), Is.EqualTo(expected));
        }

        [TestCase("null")]
        [TestCase("missing-registration")]
        [TestCase("not-installed")]
        [TestCase("missing-name")]
        [TestCase("different-name")]
        [TestCase("missing-path")]
        [TestCase("different-path")]
        [TestCase("missing-id")]
        [TestCase("different-id")]
        [TestCase("malformed-version")]
        public void CachedSourceIsNotUsedForAMissingOrMismatchedSelection(string mismatch)
        {
            var data = Snapshot();
            var version = Version();
            object selected = version;
            if (mismatch == "null") selected = null;
            if (mismatch == "missing-registration") data.Packages.Clear();
            if (mismatch == "not-installed") version.isInstalled = false;
            if (mismatch == "missing-name") version.name = null;
            if (mismatch == "different-name") version.name = "com.example.another";
            if (mismatch == "missing-path") version.localPath = null;
            if (mismatch == "different-path") version.localPath += "-another";
            if (mismatch == "missing-id") version.packageId = null;
            if (mismatch == "different-id") version.packageId += "-another";
            if (mismatch == "malformed-version") selected = new object();

            Assert.That(PackageManagerPackageStatus.GetRepositoryUrl(Root, selected, data), Is.Empty);
        }

        [Test]
        public void VerifiedSubmoduleUsesItsRegisteredSourceAndRejectsMissingRegistration()
        {
            var data = new PackageManagerPackageStatus.StatusSnapshot();
            var version = Version();
            version.localPath = Path.Combine(Root, "Packages", Name);
            version.packageId = Name + "@file:" + version.localPath;
            data.Packages.Add(Name, new PackageManagerPackageStatus.InstalledPackage(
                version.localPath, PackageSource.Embedded, version.packageId));
            data.Gitlinks.Add("Packages/" + Name);
            data.Repositories.Add("Packages/" + Name, "git@github.com:example/package.git");
            Assert.That(PackageManagerPackageStatus.GetRepositoryUrl(Root, version, data), Is.EqualTo(Repository));

            data.Repositories.Clear();
            Assert.That(PackageManagerPackageStatus.GetRepositoryUrl(Root, version, data), Is.Empty);
        }

        [Test]
        public void UnsupportedOrMissingNativeLinksNeverReceiveARepositoryUrl()
        {
            Assert.That(PackageRepositoryLink.CurrentUrl(null, Version()), Is.Empty);
            Assert.That(PackageRepositoryLink.CurrentUrl(new VisualElement(), Version()), Is.Empty);
            Assert.That(PackageRepositoryLink.CurrentUrl(new VisualElement(), null), Is.Empty);
        }

        [UnityTest]
        [Category("PackageManagerCompatibility")]
        public IEnumerator NativeLinksRefreshTracksTheHeaderSelectionAndKeepsRepositoryFirst()
        {
            VisualElement header = null;
            object manager = null;
            object activePage = null;
            PackageManagerPackageStatus.Retain();
            try
            {
                PackageManagerPackageStatus.Refresh();
                double deadline = EditorApplication.timeSinceStartup + 20;
                while (!PackageManagerPackageStatus.IsReady && EditorApplication.timeSinceStartup < deadline)
                    yield return null;
                Assert.That(PackageManagerPackageStatus.IsReady, Is.True);
                MethodInfo refresh = PackageRepositoryLink.GetRefreshTarget();
                Assert.That(refresh, Is.Not.Null);
                Assert.That(PackageManagerSubmoduleHarmonyPatch.IsPatched(refresh), Is.True);
                Type headerType = PackageManagerSubmoduleHarmonyPatch.FindType(
                    "UnityEditor.PackageManager.UI.Internal.PackageDetailsHeader");
                ConstructorInfo constructor = headerType.GetConstructor(Instance, null, Type.EmptyTypes, null);
                if (constructor != null)
                    header = (VisualElement)constructor.Invoke(null);
                else
                {
                    Type serializedType = headerType.GetNestedType("UxmlSerializedData",
                        BindingFlags.Public | BindingFlags.NonPublic);
                    Assert.That(serializedType, Is.Not.Null, "The native header needs its UXML construction factory.");
                    var serialized = (VisualElement.UxmlSerializedData)Activator.CreateInstance(serializedType, true);
                    header = (VisualElement)serialized.CreateInstance();
                }
                manager = Read(header, "m_PageManager");
                activePage = Read(manager, "activePage");
                var links = FindElement(header, refresh.DeclaringType);
                Assert.That(links, Is.Not.Null);
                object database = Read(header, "m_PackageDatabase");
                object[] versions = ((IEnumerable)Read(database, "allPackages")).Cast<object>()
                    .Select(package => Read(Read(package, "versions"), "installed")).Where(version => version != null).ToArray();
                object source = versions.FirstOrDefault(version => !string.IsNullOrEmpty(PackageManagerPackageStatus.GetRepositoryUrl(version)));
                object ordinary = versions.FirstOrDefault(version => string.IsNullOrEmpty(PackageManagerPackageStatus.GetRepositoryUrl(version)));
                Assert.That(source, Is.Not.Null, "The project needs an installed Git source for native Repository-link coverage.");
                Assert.That(ordinary, Is.Not.Null, "The project needs an ordinary installed package for selection-change coverage.");
                FieldInfo selected = headerType.GetField("m_Version", Instance);
                selected.SetValue(header, source);
                string url = PackageManagerPackageStatus.GetRepositoryUrl(source);

                refresh.Invoke(links, new[] { source });
                VisualElement row = links.Q(RowName);
                Assert.That(row, Is.Not.Null);
                Assert.That(row[0].name, Is.EqualTo(PackageRepositoryLink.ElementName));
                Assert.That(row[0].tooltip, Is.EqualTo(url));
                string[] nativeLabels = row.Children().OfType<TextElement>()
                    .Where(element => element.name != PackageRepositoryLink.ElementName && element.name != SeparatorName)
                    .Select(element => element.text).ToArray();
                refresh.Invoke(links, new[] { source });
                Assert.That(links.Query(name: PackageRepositoryLink.ElementName).ToList(), Has.Count.EqualTo(1));
                Assert.That(links.Q(RowName).Children().OfType<TextElement>()
                    .Where(element => element.name != PackageRepositoryLink.ElementName && element.name != SeparatorName)
                    .Select(element => element.text).ToArray(), Is.EqualTo(nativeLabels));

                VisualElement parent = links.parent;
                int position = parent.IndexOf(links);
                links.RemoveFromHierarchy();
                Assert.That(PackageRepositoryLink.CurrentUrl(links, source), Is.Empty,
                    "Detached links must not resolve a URL from a previous header.");
                parent.Insert(position, links);
                Assert.That(PackageRepositoryLink.CurrentUrl(links, source), Is.EqualTo(url));

                selected.SetValue(header, ordinary);
                Assert.That(PackageRepositoryLink.CurrentUrl(links, source), Is.Empty,
                    "A stale link must stop resolving as soon as the header selects another version.");
                refresh.Invoke(links, new[] { ordinary });
                Assert.That(links.Q(PackageRepositoryLink.ElementName), Is.Null);
                Assert.That(links.Q(SeparatorName), Is.Null);
                selected.SetValue(header, null);
                refresh.Invoke(links, new object[] { null });
                Assert.That(links.childCount, Is.Zero);
                Assert.That(Read(manager, "activePage"), Is.SameAs(activePage),
                    "An isolated header must not navigate the user's Package Manager.");
            }
            finally
            {
                if (header != null && manager != null)
                {
                    EventInfo changed = manager.GetType().GetEvent("onVisualStateChange", Instance);
                    MethodInfo callback = header.GetType().GetMethod("OnVisualStateChange", Instance);
                    if (changed != null && callback != null)
                        changed.RemoveEventHandler(manager, Delegate.CreateDelegate(changed.EventHandlerType, header, callback));
                }
                PackageManagerPackageStatus.Release();
            }
        }

        private static PackageManagerPackageStatus.StatusSnapshot Snapshot(string source = Url)
        {
            var data = new PackageManagerPackageStatus.StatusSnapshot();
            data.Packages.Add(Name, new PackageManagerPackageStatus.InstalledPackage(
                CachePath, PackageSource.Git, Name + "@" + source));
            return data;
        }

        private static VersionStub Version(string source = Url) => new()
        {
            name = Name, isInstalled = true, localPath = CachePath, packageId = Name + "@" + source
        };

        private static object Read(object value, string name) => PackageManagerSubmoduleHarmonyPatch.Read(value, name);

        private static VisualElement FindElement(VisualElement root, Type type)
        {
            if (root.GetType() == type)
                return root;
            foreach (VisualElement child in root.Children())
            {
                VisualElement found = FindElement(child, type);
                if (found != null)
                    return found;
            }
            return null;
        }

        private sealed class VersionStub
        {
            public string name { get; set; }
            public bool isInstalled { get; set; }
            public string localPath { get; set; }
            public string packageId { get; set; }
        }
    }
}
