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

        private sealed class ReadOnlyListDatabaseWithSource
        {
            public void UpdatePackages(
                IReadOnlyList<IFakePackage> additions,
                IReadOnlyList<string> removals,
                FakePackagesChangedSource source)
            {
            }
        }

        private sealed class AllCollectionShapesDatabase
        {
            public void UpdatePackages(
                IReadOnlyList<IFakePackage> additions,
                IReadOnlyList<string> removals,
                FakePackagesChangedSource source)
            {
            }

            public void UpdatePackages(
                IReadOnlyCollection<IFakePackage> additions,
                IReadOnlyCollection<string> removals)
            {
            }

            public void UpdatePackages(IList<IFakePackage> additions, IList<string> removals)
            {
            }
        }

        private sealed class ReadOnlyListDatabaseWithoutSource
        {
            public void UpdatePackages(
                IReadOnlyList<IFakePackage> additions,
                IReadOnlyList<string> removals)
            {
            }
        }

        private sealed class MixedReadOnlyListDatabase
        {
            public void UpdatePackages(
                IReadOnlyList<IFakePackage> additions,
                IReadOnlyCollection<string> removals,
                FakePackagesChangedSource source)
            {
            }
        }

        private sealed class WrongElementReadOnlyListDatabase
        {
            public void UpdatePackages(
                IReadOnlyList<object> additions,
                IReadOnlyList<string> removals,
                FakePackagesChangedSource source)
            {
            }
        }

        private sealed class WrongSourceReadOnlyListDatabase
        {
            public void UpdatePackages(
                IReadOnlyList<IFakePackage> additions,
                IReadOnlyList<string> removals,
                int source)
            {
            }
        }

        private sealed class WrongReturnReadOnlyListDatabase
        {
            public bool UpdatePackages(
                IReadOnlyList<IFakePackage> additions,
                IReadOnlyList<string> removals,
                FakePackagesChangedSource source) => true;
        }

        private sealed class GenericReadOnlyListDatabase
        {
            public void UpdatePackages<T>(
                IReadOnlyList<IFakePackage> additions,
                IReadOnlyList<string> removals,
                FakePackagesChangedSource source)
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

        private class ReadOnlyListRemoveAction
        {
            public bool TriggerActionImplementation(
                IReadOnlyList<IFakePackage> packages)
            {
                return packages != null;
            }
        }

        private sealed class InheritedReadOnlyListRemoveAction : ReadOnlyListRemoveAction
        {
        }

        private sealed class AllCollectionShapesRemoveAction
        {
            public bool TriggerActionImplementation(IList<IFakePackage> packages) =>
                packages != null;

            public bool TriggerActionImplementation(IReadOnlyList<IFakePackage> packages) =>
                packages != null;

            public bool TriggerActionImplementation(IReadOnlyCollection<IFakePackage> packages) =>
                packages != null;
        }

        private sealed class ReadOnlyAndLegacyListRemoveAction
        {
            public bool TriggerActionImplementation(IList<IFakePackage> packages) =>
                packages != null;

            public bool TriggerActionImplementation(IReadOnlyList<IFakePackage> packages) =>
                packages != null;
        }

        private sealed class WrongElementReadOnlyListRemoveAction
        {
            public bool TriggerActionImplementation(IReadOnlyList<object> packages) =>
                packages != null;
        }

        private sealed class AdditionalParameterReadOnlyListRemoveAction
        {
            public bool TriggerActionImplementation(
                IReadOnlyList<IFakePackage> packages,
                bool force) => packages != null;
        }

        private sealed class WrongReturnReadOnlyListRemoveAction
        {
            public void TriggerActionImplementation(IReadOnlyList<IFakePackage> packages)
            {
            }
        }

        private sealed class StaticReadOnlyListRemoveAction
        {
            public static bool TriggerActionImplementation(IReadOnlyList<IFakePackage> packages) =>
                packages != null;
        }

        private sealed class GenericReadOnlyListRemoveAction
        {
            public bool TriggerActionImplementation<T>(IReadOnlyList<IFakePackage> packages) =>
                packages != null;
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
        public void ProjectionUpdatePackages_AcceptsExactReadOnlyListShapeWithSource()
        {
            MethodInfo method = FindUpdatePackagesMethod(typeof(ReadOnlyListDatabaseWithSource));

            Assert.That(method, Is.Not.Null);
            ParameterInfo[] parameters = method.GetParameters();
            Assert.That(parameters, Has.Length.EqualTo(3));
            Assert.That(parameters[0].ParameterType, Is.EqualTo(typeof(IReadOnlyList<IFakePackage>)));
            Assert.That(parameters[1].ParameterType, Is.EqualTo(typeof(IReadOnlyList<string>)));
            Assert.That(parameters[2].ParameterType, Is.EqualTo(typeof(FakePackagesChangedSource)));
        }

        [Test]
        public void ProjectionUpdatePackages_KeepsReadOnlyCollectionPreferenceOverReadOnlyList()
        {
            MethodInfo method = FindUpdatePackagesMethod(typeof(AllCollectionShapesDatabase));

            Assert.That(method, Is.Not.Null);
            Assert.That(
                method.GetParameters()[0].ParameterType,
                Is.EqualTo(typeof(IReadOnlyCollection<IFakePackage>)));
        }

        [TestCase(typeof(ReadOnlyListDatabaseWithoutSource))]
        [TestCase(typeof(MixedReadOnlyListDatabase))]
        [TestCase(typeof(WrongElementReadOnlyListDatabase))]
        [TestCase(typeof(WrongSourceReadOnlyListDatabase))]
        [TestCase(typeof(WrongReturnReadOnlyListDatabase))]
        [TestCase(typeof(GenericReadOnlyListDatabase))]
        public void ProjectionUpdatePackages_RejectsUnverifiedReadOnlyListShapes(Type databaseType)
        {
            Assert.That(FindUpdatePackagesMethod(databaseType), Is.Null);
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
                Is.True);
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

        [Test]
        public void RemoveActionCollection_AcceptsExactReadOnlyListShape()
        {
            MethodInfo method = PackageManagerSubmoduleHarmonyPatch
                .FindRemoveActionCollectionTargetMethod(
                    typeof(ReadOnlyListRemoveAction),
                    typeof(IFakePackage));

            Assert.That(method, Is.Not.Null);
            Assert.That(method.DeclaringType, Is.EqualTo(typeof(ReadOnlyListRemoveAction)));
            Assert.That(
                method.GetParameters()[0].ParameterType,
                Is.EqualTo(typeof(IReadOnlyList<IFakePackage>)));
        }

        [Test]
        public void RemoveActionCollection_KeepsReadOnlyCollectionPreferenceOverReadOnlyList()
        {
            MethodInfo method = PackageManagerSubmoduleHarmonyPatch
                .FindRemoveActionCollectionTargetMethod(
                    typeof(AllCollectionShapesRemoveAction),
                    typeof(IFakePackage));

            Assert.That(method, Is.Not.Null);
            Assert.That(
                method.GetParameters()[0].ParameterType,
                Is.EqualTo(typeof(IReadOnlyCollection<IFakePackage>)));
        }

        [Test]
        public void RemoveActionCollection_PrefersReadOnlyListOverLegacyList()
        {
            MethodInfo method = PackageManagerSubmoduleHarmonyPatch
                .FindRemoveActionCollectionTargetMethod(
                    typeof(ReadOnlyAndLegacyListRemoveAction),
                    typeof(IFakePackage));

            Assert.That(method, Is.Not.Null);
            Assert.That(
                method.GetParameters()[0].ParameterType,
                Is.EqualTo(typeof(IReadOnlyList<IFakePackage>)));
        }

        [TestCase(typeof(WrongElementReadOnlyListRemoveAction))]
        [TestCase(typeof(AdditionalParameterReadOnlyListRemoveAction))]
        [TestCase(typeof(WrongReturnReadOnlyListRemoveAction))]
        [TestCase(typeof(StaticReadOnlyListRemoveAction))]
        [TestCase(typeof(GenericReadOnlyListRemoveAction))]
        [TestCase(typeof(InheritedReadOnlyListRemoveAction))]
        public void RemoveActionCollection_RejectsUnverifiedReadOnlyListShapes(Type actionType)
        {
            Assert.That(
                PackageManagerSubmoduleHarmonyPatch.FindRemoveActionCollectionTargetMethod(
                    actionType,
                    typeof(IFakePackage)),
                Is.Null);
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
