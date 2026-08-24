using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;

namespace MartinCalander.GitSubmoduleManager.Editor.Tests
{
    [Category(PackageManagerCompatibilityContractTests.CategoryName)]
    public sealed class PackageManagerUnityVersionCollectionContractTests
    {
        private const BindingFlags StaticMembers =
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        private interface IFakePackage
        {
        }

        private enum FakePackagesChangedSource
        {
            Other
        }

        private sealed class LegacyListDatabase
        {
            public void UpdatePackages(
                IList<IFakePackage> additions,
                IList<string> removals)
            {
            }
        }

        private sealed class LegacyListDatabaseWithSource
        {
            public void UpdatePackages(
                IList<IFakePackage> additions,
                IList<string> removals)
            {
            }

            public void UpdatePackages(
                IList<IFakePackage> additions,
                IList<string> removals,
                FakePackagesChangedSource source)
            {
            }
        }

        private sealed class BothCollectionShapesDatabase
        {
            public void UpdatePackages(
                IList<IFakePackage> additions,
                IList<string> removals,
                FakePackagesChangedSource source)
            {
            }

            public void UpdatePackages(
                IReadOnlyCollection<IFakePackage> additions,
                IReadOnlyCollection<string> removals)
            {
            }
        }

        private sealed class BroadCollectionDatabase
        {
            public void UpdatePackages(
                IEnumerable<IFakePackage> additions,
                IEnumerable<string> removals)
            {
            }
        }

        private sealed class MixedCollectionDatabase
        {
            public void UpdatePackages(
                IReadOnlyCollection<IFakePackage> additions,
                IList<string> removals)
            {
            }
        }

        private sealed class LegacyListRemoveAction
        {
            public bool TriggerActionImplementation(
                IList<IFakePackage> packages)
            {
                return packages != null;
            }
        }

        private sealed class BothCollectionShapesRemoveAction
        {
            public bool TriggerActionImplementation(
                IList<IFakePackage> packages)
            {
                return packages != null;
            }

            public bool TriggerActionImplementation(
                IReadOnlyCollection<IFakePackage> packages)
            {
                return packages != null;
            }
        }

        [Test]
        public void ProjectionUpdatePackages_AcceptsExactLegacyListShape()
        {
            MethodInfo method = FindUpdatePackagesMethod(
                typeof(LegacyListDatabase));

            Assert.That(method, Is.Not.Null);
            ParameterInfo[] parameters = method.GetParameters();
            Assert.That(
                parameters[0].ParameterType.GetGenericTypeDefinition(),
                Is.EqualTo(typeof(IList<>)));
            Assert.That(
                parameters[1].ParameterType.GetGenericTypeDefinition(),
                Is.EqualTo(typeof(IList<>)));
        }

        [Test]
        public void ProjectionUpdatePackages_PreservesSourceOverloadPreference()
        {
            MethodInfo method = FindUpdatePackagesMethod(
                typeof(LegacyListDatabaseWithSource));

            Assert.That(method, Is.Not.Null);
            Assert.That(method.GetParameters(), Has.Length.EqualTo(3));
        }

        [Test]
        public void ProjectionUpdatePackages_PrefersCurrentReadOnlyShape()
        {
            MethodInfo method = FindUpdatePackagesMethod(
                typeof(BothCollectionShapesDatabase));

            Assert.That(method, Is.Not.Null);
            ParameterInfo[] parameters = method.GetParameters();
            Assert.That(parameters, Has.Length.EqualTo(2));
            Assert.That(
                parameters[0].ParameterType.GetGenericTypeDefinition(),
                Is.EqualTo(typeof(IReadOnlyCollection<>)));
            Assert.That(
                parameters[1].ParameterType.GetGenericTypeDefinition(),
                Is.EqualTo(typeof(IReadOnlyCollection<>)));
        }

        [Test]
        public void ProjectionUpdatePackages_RejectsBroadOrMixedShapes()
        {
            Assert.That(
                FindUpdatePackagesMethod(typeof(BroadCollectionDatabase)),
                Is.Null);
            Assert.That(
                FindUpdatePackagesMethod(typeof(MixedCollectionDatabase)),
                Is.Null);
        }

        [Test]
        public void RemoveActionCollection_AcceptsOnlyExactKnownShapes()
        {
            Type packageInterface =
                PackageManagerSubmoduleHarmonyPatch.FindLoadedType(
                    PackageManagerSubmoduleHarmonyPatch.PackageInterfaceTypeName);
            Assert.That(packageInterface, Is.Not.Null);

            Assert.That(
                IsSupportedCollection(typeof(IReadOnlyCollection<>), packageInterface),
                Is.True);
            Assert.That(
                IsSupportedCollection(typeof(IList<>), packageInterface),
                Is.True);
            Assert.That(
                IsSupportedCollection(typeof(IEnumerable<>), packageInterface),
                Is.False);
            Assert.That(
                IsSupportedCollection(typeof(ICollection<>), packageInterface),
                Is.False);
            Assert.That(
                IsSupportedCollection(typeof(IReadOnlyList<>), packageInterface),
                Is.False);
            Assert.That(
                IsSupportedCollection(typeof(List<>), packageInterface),
                Is.False);
        }

        [Test]
        public void RemoveActionCollection_PrefersCurrentReadOnlyShape()
        {
            MethodInfo preferred = PackageManagerSubmoduleHarmonyPatch
                .FindRemoveActionCollectionTargetMethod(
                    typeof(BothCollectionShapesRemoveAction),
                    typeof(IFakePackage));
            MethodInfo legacy = PackageManagerSubmoduleHarmonyPatch
                .FindRemoveActionCollectionTargetMethod(
                    typeof(LegacyListRemoveAction),
                    typeof(IFakePackage));

            Assert.That(preferred, Is.Not.Null);
            Assert.That(
                preferred.GetParameters()[0].ParameterType
                    .GetGenericTypeDefinition(),
                Is.EqualTo(typeof(IReadOnlyCollection<>)));
            Assert.That(legacy, Is.Not.Null);
            Assert.That(
                legacy.GetParameters()[0].ParameterType
                    .GetGenericTypeDefinition(),
                Is.EqualTo(typeof(IList<>)));
        }

        private static MethodInfo FindUpdatePackagesMethod(Type databaseType)
        {
            Type contractType = typeof(PackageManagerGitHubPackageProjection)
                .GetNestedType("ReflectionContract", BindingFlags.NonPublic);
            Assert.That(contractType, Is.Not.Null);
            MethodInfo finder = contractType.GetMethod(
                "FindUpdatePackagesMethod",
                StaticMembers);
            Assert.That(finder, Is.Not.Null);
            return (MethodInfo)finder.Invoke(
                null,
                new object[] { databaseType, typeof(IFakePackage) });
        }

        private static bool IsSupportedCollection(
            Type genericTypeDefinition,
            Type packageInterface)
        {
            return PackageManagerSubmoduleHarmonyPatch
                .IsSupportedPackageCollectionType(
                    genericTypeDefinition.MakeGenericType(packageInterface));
        }
    }
}
