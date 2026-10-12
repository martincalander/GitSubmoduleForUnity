#if UNITY_7000_0_OR_NEWER && ENABLE_CORECLR
using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;

namespace MartinCalander.GitSubmoduleManager.Editor.Tests
{
    [TestFixture]
    [Category("PackageManagerCompatibility")]
    public sealed class CoreClrAssemblyCompatibilityTests
    {
        [TestCase("Mono.Cecil")]
        [TestCase("Mono.Cecil.Mdb")]
        [TestCase("Mono.Cecil.Pdb")]
        [TestCase("Mono.Cecil.Rocks")]
        public void CecilAssemblies_LoadOneMatchingVersionWithResolvableTypes(string assemblyName)
        {
            Assembly[] matches = EditorAssemblyCompatibility.GetLoadedAssemblies()
                .Where(assembly => string.Equals(
                    assembly.GetName().Name,
                    assemblyName,
                    StringComparison.Ordinal))
                .ToArray();

            Assert.That(matches, Has.Length.EqualTo(1),
                "Unity must select one current " + assemblyName + " assembly. Found: " +
                string.Join(", ", matches.Select(assembly => assembly.FullName)));

            Assembly selected = matches[0];
            Assert.That(selected.GetName().Version, Is.EqualTo(new Version(0, 11, 6, 0)),
                "Cecil and its symbol providers must use the same supported version.");

            try
            {
                Assert.That(selected.GetTypes(), Is.Not.Empty,
                    "The selected Cecil assembly must expose its implementation types.");
            }
            catch (ReflectionTypeLoadException exception)
            {
                Assert.Fail(selected.FullName + " contains types Unity cannot load:\n" +
                    string.Join("\n", exception.LoaderExceptions
                        .Where(loaderException => loaderException != null)
                        .Select(loaderException => loaderException.ToString())));
            }
        }
    }
}
#endif
