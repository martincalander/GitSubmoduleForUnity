using System;
using System.Collections.Generic;
using System.Reflection;

namespace MartinCalander.GitSubmoduleManager.Editor
{
    internal static class EditorAssemblyCompatibility
    {
        internal static IReadOnlyList<Assembly> GetLoadedAssemblies()
        {
#if UNITY_7000_0_OR_NEWER
            // CoreCLR can retain assemblies from contexts Unity already unloaded.
            return UnityEngine.Assemblies.CurrentAssemblies.GetLoadedAssemblies();
#else
            return AppDomain.CurrentDomain.GetAssemblies();
#endif
        }
    }
}
