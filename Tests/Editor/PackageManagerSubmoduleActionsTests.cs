using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine.TestTools;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;
using NUnit.Framework;
using UnityEditor.UIElements;
using UnityEditor.PackageManager;
using UnityEngine;
using UnityEngine.UIElements;

namespace MartinCalander.GitSubmoduleManager.Editor.Tests
{
    [Category("PackageSourceFeatures")]
    public sealed class PackageManagerSubmoduleActionsTests
    {
        private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        [Test]
        [Category("PackageManagerCompatibility")]
        public void NativeAddMenuHasOneOwnedPatchAfterRepeatedInstallation()
        {
            MethodInfo target = PackageManagerSubmoduleActions.GetAddMenuTarget();
            Assert.That(target, Is.Not.Null, "The native add menu contract changed.");
            for (int attempt = 0; attempt < 3; attempt++)
                PackageManagerSubmoduleHarmonyPatch.Install();
            Assert.That(PackageManagerSubmoduleHarmonyPatch.IsPatched(target), Is.True);
            object harmony = typeof(PackageManagerSubmoduleHarmonyPatch)
                .GetField("harmony", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            object info = harmony.GetType().GetMethod("GetPatchInfo", BindingFlags.Static | BindingFlags.Public)
                .Invoke(null, new object[] { target });
            var postfixes = ((IEnumerable)PackageManagerSubmoduleHarmonyPatch.Read(info, "Postfixes")).Cast<object>();
            Assert.That(postfixes.Count(patch => (string)PackageManagerSubmoduleHarmonyPatch.Read(patch, "owner") ==
                PackageManagerSubmoduleHarmonyPatch.HarmonyId), Is.EqualTo(1));
        }

        [Test]
        [Category("PackageManagerCompatibility")]
        public void NativeAddMenuKeepsOtherActionsAndRemovesOnlyItsOwnEntry()
        {
            VisualElement menu = CreateNativeElement("ExtendableToolbarMenu");
            Type menuType = menu.GetType();
            MethodInfo add = menuType.GetMethod("AddDropdownItem", Instance);
            object original = add.Invoke(menu, null);
            Action originalAction = () => { };
            original.GetType().GetProperty("text", Instance).SetValue(original, "Existing action");
            original.GetType().GetProperty("action", Instance).SetValue(original, originalAction);
            IList Items() => (IList)PackageManagerSubmoduleHarmonyPatch.Read(menu, "m_DropdownItems");
            int before = Items().Count;
            try
            {
                Assert.That(PackageManagerSubmoduleActions.AttachMenu(menu), Is.True);
                Assert.That(PackageManagerSubmoduleActions.AttachMenu(menu), Is.True);
                Assert.That(Items().Count, Is.EqualTo(before + 1));
                object item = Items().Cast<object>().Single(entry =>
                    (string)PackageManagerSubmoduleHarmonyPatch.Read(entry, "text") == PackageManagerSubmoduleActions.InstallMenuText);
                Assert.That(PackageManagerSubmoduleHarmonyPatch.Read(item, "action"), Is.InstanceOf<Action>());
                Assert.That(PackageManagerSubmoduleHarmonyPatch.Read(item, "visible"), Is.True);
                Assert.That(PackageManagerSubmoduleHarmonyPatch.Read(item, "enabled"), Is.True);

                PackageManagerSubmoduleActions.RemoveMenu(menu);
                PackageManagerSubmoduleActions.RemoveMenu(menu);
                Assert.That(Items().Count, Is.EqualTo(before));
                Assert.That(Items().Contains(original), Is.True);
                Assert.That(PackageManagerSubmoduleHarmonyPatch.Read(original, "action"), Is.SameAs(originalAction));
                Assert.That(PackageManagerSubmoduleHarmonyPatch.Read(item, "action"), Is.Null,
                    "A removed menu must release its callback.");
                Assert.That(PackageManagerSubmoduleActions.AttachMenu(menu), Is.True,
                    "A rebuilt or reattached toolbar must accept the action again.");
            }
            finally { PackageManagerSubmoduleActions.RemoveMenu(menu); }
        }

        [Test]
        [Category("PackageManagerCompatibility")]
        public void InstallInputUsesNativeSingleFieldPopupAndSubmitsTheEnteredUrl()
        {
            string submitted = null;
            object args = PackageManagerSubmoduleActions.CreateInputArguments(value => submitted = value);
            Assert.That(args, Is.Not.Null);
            Assert.That(PackageManagerSubmoduleHarmonyPatch.Read(args, "title"), Is.EqualTo("Install package as Git submodule"));
            Assert.That(PackageManagerSubmoduleHarmonyPatch.Read(args, "submitButtonText"), Is.EqualTo("Install"));
            const string url = "https://example.com/team/package.git#develop";
            ((Action<string>)PackageManagerSubmoduleHarmonyPatch.Read(args, "onInputSubmitted"))(url);
            Assert.That(submitted, Is.EqualTo(url));
        }

        [Test]
        public void UnrelatedMenusAndUnknownPackageSelectionsAreNotMutationEntrypoints()
        {
            Assert.That(PackageManagerSubmoduleActions.AttachMenu(null), Is.False);
            Assert.That(PackageManagerSubmoduleActions.AttachMenu(new ToolbarMenu()), Is.False);
            Assert.That(PackageManagerSubmoduleActions.RequestRemoval(null), Is.False);
            Assert.That(PackageManagerSubmoduleActions.RequestRemoval(new VersionStub
            {
                name = "com.example.package",
                localPath = "/project/Packages/com.example.package"
            }), Is.False);
            Assert.That(PackageManagerSubmoduleActions.RequestRemoval(new VersionStub { isInstalled = true }), Is.False);
        }

        [Test]
        public void RemovalDetectsInstalledDependentsBeforeChangingGitState()
        {
            PackageInfo Package(string name, string dependency) => JsonUtility.FromJson<PackageInfo>(
                "{\"m_Name\":\"" + name + "\",\"m_Dependencies\":[{\"m_Name\":\"" + dependency + "\",\"m_Version\":\"1.0.0\"}]}");
            var packages = new[]
            {
                Package("com.example.consumer", "com.example.target"),
                Package("com.example.unrelated", "com.example.other"),
                Package("com.example.target", "com.example.target")
            };
            Assert.That(PackageManagerSubmoduleActions.FindDependents("com.example.target", packages),
                Is.EqualTo(new[] { "com.example.consumer" }));
            Assert.That(PackageManagerSubmoduleActions.FindDependents("com.example.missing", packages), Is.Empty);
        }

        [Test]
        [Category("PackageManagerCompatibility")]
        public void ARemovedNativeItemIsRepairedWithoutDuplicatingOtherActions()
        {
            VisualElement menu = CreateNativeElement("ExtendableToolbarMenu");
            try
            {
                Assert.That(PackageManagerSubmoduleActions.AttachMenu(menu), Is.True);
                object first = OwnedItem(menu);
                menu.GetType().GetMethod("Remove", Instance, null, new[] { first.GetType() }, null)
                    .Invoke(menu, new[] { first });
                Assert.That(PackageManagerSubmoduleActions.AttachMenu(menu), Is.True);
                Assert.That(OwnedItems(menu).Count(), Is.EqualTo(1));
                Assert.That(OwnedItem(menu), Is.Not.SameAs(first));
                Assert.That(PackageManagerSubmoduleHarmonyPatch.Read(first, "action"), Is.Null);
            }
            finally { PackageManagerSubmoduleActions.RemoveMenu(menu); }
        }

        [UnityTest]
        [Category("PackageManagerCompatibility")]
        public IEnumerator MenuDetachReattachCyclesRepairTheActionAndDisposalStopsRepair()
        {
            VisualElement menu = CreateNativeElement("ExtendableToolbarMenu");
            var window = ScriptableObject.CreateInstance<SubmoduleActionTestWindow>();
            int baseline = ActiveMenuCount();
            try
            {
                window.ShowUtility();
                window.rootVisualElement.Add(menu);
                yield return null;
                Assert.That(menu.panel, Is.Not.Null);
                Assert.That(PackageManagerSubmoduleActions.AttachMenu(menu), Is.True);
                for (int cycle = 0; cycle < 4; cycle++)
                {
                    Assert.That(ActiveMenuCount(), Is.EqualTo(baseline + 1));
                    object oldItem = OwnedItem(menu);
                    menu.RemoveFromHierarchy();
                    Assert.That(ActiveMenuCount(), Is.EqualTo(baseline));
                    Assert.That(OwnedItems(menu), Is.Empty);
                    Assert.That(PackageManagerSubmoduleHarmonyPatch.Read(oldItem, "action"), Is.Null);
                    window.rootVisualElement.Add(menu);
                    Assert.That(OwnedItems(menu).Count(), Is.EqualTo(1));
                }
                PackageManagerSubmoduleActions.RemoveMenu(menu);
                menu.RemoveFromHierarchy();
                window.rootVisualElement.Add(menu);
                Assert.That(OwnedItems(menu), Is.Empty, "Disposed menus must not register again on attachment.");
                Assert.That(ActiveMenuCount(), Is.EqualTo(baseline));
            }
            finally
            {
                PackageManagerSubmoduleActions.RemoveMenu(menu);
                window.Close();
            }
        }

        [Test]
        [Category("PackageManagerCompatibility")]
        public void CachedToolbarLookupDoesNotScaleWithWindowHierarchyAndRepairsReplacement()
        {
            var root = new VisualElement();
            var padding = new VisualElement();
            VisualElement toolbar = CreateNativeElement("PackageManagerToolbar");
            VisualElement replacement = null;
            root.Add(padding);
            root.Add(toolbar);
            try
            {
                PackageManagerSubmoduleActions.AttachInRoot(root);
                VisualElement menu = PackageManagerSubmoduleHarmonyPatch.Read(toolbar, "addMenu") as VisualElement;
                Assert.That(OwnedItems(menu).Count(), Is.EqualTo(1));
                long Measure()
                {
                    for (int warm = 0; warm < 12; warm++)
                        PackageManagerSubmoduleActions.AttachInRoot(root);
                    long start = GC.GetAllocatedBytesForCurrentThread();
                    for (int iteration = 0; iteration < 100; iteration++)
                        PackageManagerSubmoduleActions.AttachInRoot(root);
                    return GC.GetAllocatedBytesForCurrentThread() - start;
                }
                long compactBytes = Measure();
                for (int item = 0; item < 2000; item++)
                    padding.Add(new VisualElement());
                long largeBytes = Measure();
                TestContext.WriteLine("100 warm toolbar observations: compact=" + compactBytes + " bytes; 2000 extra elements=" + largeBytes + " bytes.");
                Assert.That(largeBytes, Is.LessThan(16384));
                Assert.That(largeBytes, Is.LessThanOrEqualTo(compactBytes + 4096), "Cached observations must not traverse unrelated UI.");

                toolbar.RemoveFromHierarchy();
                replacement = CreateNativeElement("PackageManagerToolbar");
                root.Add(replacement);
                PackageManagerSubmoduleActions.AttachInRoot(root);
                VisualElement replacementMenu = PackageManagerSubmoduleHarmonyPatch.Read(replacement, "addMenu") as VisualElement;
                Assert.That(OwnedItems(replacementMenu).Count(), Is.EqualTo(1), "A replaced toolbar must invalidate the cached lookup.");
            }
            finally
            {
                PackageManagerSubmoduleActions.RemoveMenu(PackageManagerSubmoduleHarmonyPatch.Read(toolbar, "addMenu") as VisualElement);
                PackageManagerSubmoduleActions.RemoveMenu(PackageManagerSubmoduleHarmonyPatch.Read(replacement, "addMenu") as VisualElement);
            }
        }

        [TestCase("reload")]
        [TestCase("refresh")]
        [TestCase("callbacks")]
        public void FailedOperationSetupReleasesOnlyResourcesItOwns(string failure)
        {
            var events = new List<string>();
            Action<bool> Resource(string name) => acquire =>
            {
                events.Add((acquire ? "+" : "-") + name);
                if (acquire && name == failure)
                    throw new InvalidOperationException("Failed " + name);
            };
            var resources = new PackageManagerSubmoduleActions.OperationResources(Resource("reload"), Resource("refresh"), Resource("callbacks"));
            Assert.Throws<InvalidOperationException>(resources.Acquire);
            string[] expected = failure == "reload" ? new[] { "+reload" } : failure == "refresh"
                ? new[] { "+reload", "+refresh", "-reload" }
                : new[] { "+reload", "+refresh", "+callbacks", "-callbacks", "-refresh", "-reload" };
            Assert.That(events, Is.EqualTo(expected));
            resources.Dispose();
            Assert.That(events, Is.EqualTo(expected), "Repeated cleanup must not release someone else's lock.");
        }

        [TestCase("callbacks")]
        [TestCase("refresh")]
        [TestCase("reload")]
        public void CleanupFailureStillAttemptsEveryOtherOwnedRelease(string failure)
        {
            var releases = new List<string>();
            Action<bool> Resource(string name) => acquire =>
            {
                if (acquire)
                    return;
                releases.Add(name);
                if (name == failure)
                    throw new InvalidOperationException("Failed " + name);
            };
            var resources = new PackageManagerSubmoduleActions.OperationResources(Resource("reload"), Resource("refresh"), Resource("callbacks"));
            resources.Acquire();
            Assert.Throws<InvalidOperationException>(resources.Dispose);
            Assert.That(releases, Is.EqualTo(new[] { "callbacks", "refresh", "reload" }));
            Assert.DoesNotThrow(resources.Dispose);
            Assert.That(releases, Has.Count.EqualTo(3));
        }

        [Test]
        public void RepeatedAcquireCannotDuplicateLocksOrCallbacks()
        {
            int acquired = 0;
            int released = 0;
            void Resource(bool acquire) { if (acquire) acquired++; else released++; }
            using (var resources = new PackageManagerSubmoduleActions.OperationResources(Resource, Resource, Resource))
            {
                resources.Acquire();
                Assert.Throws<InvalidOperationException>(resources.Acquire);
                Assert.That(acquired, Is.EqualTo(3));
            }
            Assert.That(released, Is.EqualTo(3));
        }

        [Test]
        public void WorkerFailuresRetainRecoveryGuidanceAndCancellationIsAnError()
        {
            var recovery = new InvalidOperationException("Checkout retained in the recovery folder.", new IOException("Git failed."));
            Assert.That(PackageManagerSubmoduleActions.GetOperationError(Task.FromException<string>(recovery)), Is.SameAs(recovery));
            Assert.That(PackageManagerSubmoduleActions.GetOperationError(Task.FromCanceled<string>(new CancellationToken(true))),
                Is.InstanceOf<OperationCanceledException>());
            Assert.That(PackageManagerSubmoduleActions.GetOperationError(Task.FromResult("com.example.package")), Is.Null);
        }

        [TestCase("installed", false)]
        [TestCase("missing", true)]
        [TestCase("registry", true)]
        [TestCase("different-path", true)]
        [TestCase("invalid-path", true)]
        [TestCase("package-error", true)]
        [TestCase("removed", false)]
        [TestCase("reintroduced", true)]
        public void ResolutionRequiresTheExpectedPackageSourceAndReportsFailures(string outcome, bool errorExpected)
        {
            const string name = "com.example.package";
            string project = Path.Combine(Path.GetTempPath(), "submodule-operation-test");
            var package = JsonUtility.FromJson<PackageInfo>("{}");
            typeof(PackageInfo).GetField("m_Name", Instance).SetValue(package, name);
            typeof(PackageInfo).GetField("m_Source", Instance).SetValue(package,
                outcome == "registry" ? PackageSource.Registry : PackageSource.Embedded);
            typeof(PackageInfo).GetField("m_ResolvedPath", Instance).SetValue(package,
                outcome == "invalid-path" ? "\0" : Path.Combine(project, "Packages", outcome == "different-path" ? "com.example.other" : name));
            if (outcome == "package-error")
            {
                var packageError = JsonUtility.FromJson<Error>("{\"m_Message\":\"Missing dependency\"}");
                typeof(PackageInfo).GetField("m_Errors", Instance).SetValue(package, new[] { packageError });
            }
            bool installing = outcome != "removed" && outcome != "reintroduced";
            PackageInfo[] packages = outcome == "missing" || outcome == "removed" ? Array.Empty<PackageInfo>() : new[] { package };
            Exception error = PackageManagerSubmoduleActions.ValidateResolvedPackage(name, installing, project, packages);
            Assert.That(error != null, Is.EqualTo(errorExpected));
            if (outcome == "package-error")
                Assert.That(error.Message, Does.Contain("Missing dependency"));
        }

        private static VisualElement CreateNativeElement(string name)
        {
            Type type = PackageManagerSubmoduleHarmonyPatch.FindType("UnityEditor.PackageManager.UI.Internal." + name);
            Assert.That(type, Is.Not.Null);
            ConstructorInfo constructor = type.GetConstructor(Instance, null, Type.EmptyTypes, null);
            if (constructor != null)
                return (VisualElement)constructor.Invoke(null);
            Type serializedType = type.GetNestedType("UxmlSerializedData", BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(serializedType, Is.Not.Null, "The native element needs its UXML construction factory.");
            var serialized = (VisualElement.UxmlSerializedData)Activator.CreateInstance(serializedType, true);
            return (VisualElement)serialized.CreateInstance();
        }

        private static IEnumerable<object> OwnedItems(VisualElement menu) =>
            ((IEnumerable)PackageManagerSubmoduleHarmonyPatch.Read(menu, "m_DropdownItems")).Cast<object>()
                .Where(entry => (string)PackageManagerSubmoduleHarmonyPatch.Read(entry, "text") == PackageManagerSubmoduleActions.InstallMenuText);

        private static object OwnedItem(VisualElement menu) => OwnedItems(menu).Single();

        private static int ActiveMenuCount() => ((IDictionary)typeof(PackageManagerSubmoduleActions)
            .GetField("Entries", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null)).Count;

        private sealed class SubmoduleActionTestWindow : EditorWindow { }

        private sealed class VersionStub
        {
            public string name { get; set; }
            public string localPath { get; set; }
            public bool isInstalled { get; set; }
        }
    }
}
