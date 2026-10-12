using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using NUnit.Framework;
using UnityEditor;

namespace MartinCalander.GitSubmoduleManager.Editor.Tests
{
    [Parallelizable(ParallelScope.None)]
    public sealed class BoundedRegularFileTests
    {
        private string directory;
        private string runtimeExecutable;
        private string executable;
        private string packageAssemblyPath;
        private string engineAssemblyDirectory;

        [OneTimeSetUp]
        public void CreateIsolatedReader()
        {
            directory = Path.Combine(Path.GetTempPath(), "GitSubmoduleRegularFileTests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            packageAssemblyPath = GetAssemblyPath(typeof(GitUtility).Assembly);
            string engineAssemblyPath = GetAssemblyPath(typeof(UnityEngine.Object).Assembly);
            Assert.That(File.Exists(packageAssemblyPath) && File.Exists(engineAssemblyPath), Is.True,
                "The isolated reader must load the same package and Unity assemblies as this Editor.");
            engineAssemblyDirectory = Path.GetDirectoryName(engineAssemblyPath);
            string source = Path.Combine(directory, "Reader.cs");
            File.WriteAllText(source, ReaderSource);
            string contents = EditorApplication.applicationContentsPath;
#if UNITY_7000_0_OR_NEWER && ENABLE_CORECLR
            string sdk = Path.Combine(contents, "Resources", "Scripting", "DotNetSdk");
            if (!Directory.Exists(sdk))
                sdk = Path.Combine(contents, "DotNetSdk");
            runtimeExecutable = Path.Combine(sdk,
                RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "dotnet.exe" : "dotnet");
            Assert.That(File.Exists(runtimeExecutable), Is.True,
                "The Editor's bundled .NET SDK must be discovered before running isolated filesystem regressions.");
            string project = Path.Combine(directory, "Reader.csproj");
            File.WriteAllText(project,
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>" +
                "<OutputType>Exe</OutputType><TargetFramework>net" + Environment.Version.Major + ".0</TargetFramework>" +
                "<NuGetAudit>false</NuGetAudit>" +
                "</PropertyGroup></Project>");
            File.WriteAllText(Path.Combine(directory, "NuGet.Config"),
                "<configuration><packageSources><clear /></packageSources></configuration>");
            string output = Path.Combine(directory, "output");
            executable = Path.Combine(output, "Reader.dll");
            CommandResult compiled = CliCommandRunner.Run(runtimeExecutable,
                new[] { "build", project, "--configuration", "Release", "--output", output,
                    "--nologo", "--verbosity", "quiet" }, directory, 30000);
#else
            string runtime = Path.Combine(contents, "Resources", "Scripting", "MonoBleedingEdge");
            if (!Directory.Exists(runtime))
                runtime = Path.Combine(contents, "MonoBleedingEdge");
            runtimeExecutable = Path.Combine(runtime, "bin", RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "mono.exe" : "mono");
            string compiler = Path.Combine(runtime, "lib", "mono", "4.5", "mcs.exe");
            Assert.That(File.Exists(runtimeExecutable) && File.Exists(compiler), Is.True,
                "The Editor's bundled Mono/compiler must be discovered before running isolated filesystem regressions.");
            executable = Path.Combine(directory, "Reader.exe");
            CommandResult compiled = CliCommandRunner.Run(runtimeExecutable,
                GitUtility.Quote(compiler) + " -out:" + GitUtility.Quote(executable) + " " + GitUtility.Quote(source),
                directory, 20000);
#endif
            Assert.That(compiled.IsSuccess, Is.True, compiled.StdOut + compiled.StdErr);
        }

        [OneTimeTearDown]
        public void RemoveIsolatedReader()
        {
            if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
                Directory.Delete(directory, true);
        }

        [Test]
        public void Read_AcceptsExactBoundAndBinaryBytes_RejectsOversizeAndCancellation()
        {
            string path = Path.Combine(directory, Guid.NewGuid().ToString("N"));
            byte[] expected = { 0, 255, 1, 128 };
            File.WriteAllBytes(path, expected);
            Assert.That(BoundedRegularFile.TryRead(path, 4, out byte[] actual, out string error), Is.True, error);
            Assert.That(actual, Is.EqualTo(expected));
            Assert.That(BoundedRegularFile.TryRead(path, 3, out actual, out error), Is.False);
            Assert.That(actual, Is.Empty);
            Assert.That(error, Does.Contain("size limit"));
            Assert.Throws<OperationCanceledException>(() => BoundedRegularFile.TryRead(
                path, 4, out _, out _, new CancellationToken(true)));
        }

        [Test]
        public void NativeReader_AcceptsRegularFileInBoundedSubprocess()
        {
            string path = Path.Combine(directory, Guid.NewGuid().ToString("N"));
            File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
            CommandResult result = RunIsolatedReader(path, "bounded");
            Assert.That(result.ExitCode, Is.EqualTo(1), result.StdOut + result.StdErr);
            Assert.That(result.TerminationConfirmed, Is.True);
            Assert.That(result.StdOut, Does.Contain("accepted"));
        }

        [Test]
        public void Read_AcceptsEmptyRegularFile_RejectsMissingAndDirectory()
        {
            string path = Path.Combine(directory, Guid.NewGuid().ToString("N"));
            Assert.That(BoundedRegularFile.TryRead(path, 0, out _, out _), Is.False);
            File.WriteAllBytes(path, Array.Empty<byte>());
            Assert.That(BoundedRegularFile.TryRead(path, 0, out byte[] bytes, out string error), Is.True, error);
            Assert.That(bytes, Is.Empty);
            Assert.That(BoundedRegularFile.TryRead(directory, 4096, out _, out _), Is.False);
        }

        [Test]
        public void OpenedReadWrite_PreservesExactBytesAndTruncatesOldSuffix()
        {
            string path = Path.Combine(directory, Guid.NewGuid().ToString("N"));
            File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4, 5 });
            using (BoundedRegularFile.OpenedFile file = BoundedRegularFile.Open(path, writable: true))
            {
                Assert.That(file.Read(5, CancellationToken.None), Has.Length.EqualTo(5));
                file.Write(new byte[] { 255, 0 });
                Assert.That(file.Read(5, CancellationToken.None), Is.EqualTo(new byte[] { 255, 0 }));
                file.Write(Array.Empty<byte>());
                Assert.That(file.Read(0, CancellationToken.None), Is.Empty);
            }
            Assert.That(File.ReadAllBytes(path), Is.Empty);
        }

        [Test]
        public void OpenedReadWrite_PreservesExclusiveSharingForManagedWriters()
        {
            string path = Path.Combine(directory, Guid.NewGuid().ToString("N"));
            File.WriteAllText(path, "preserve me");
            using (BoundedRegularFile.OpenedFile file = BoundedRegularFile.Open(path, writable: true))
            {
                Assert.Throws<IOException>(() =>
                {
                    using (var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
                        writer.WriteByte(1);
                });
            }
            Assert.That(File.ReadAllText(path), Is.EqualTo("preserve me"));
        }

        [Test]
        public void OpenedHandle_RejectsChangedContentsEvenWhenLengthAndMtimeMatch()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                Assert.Ignore("Windows prevents the concurrent writer through the opened handle's sharing mode.");
            string path = Path.Combine(directory, Guid.NewGuid().ToString("N"));
            File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
            // Use an exact-second timestamp so DateTime preserves all native precision.
            DateTime originalTime = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(path, originalTime);
            using (BoundedRegularFile.OpenedFile file = BoundedRegularFile.Open(path, writable: true))
            {
                CommandResult changed = RunIsolatedReader(path, "edit");
                Assert.That(changed.IsSuccess, Is.True, changed.StdOut + changed.StdErr);
                Assert.That(changed.TerminationConfirmed, Is.True);
                Assert.That(new FileInfo(path).Length, Is.EqualTo(3));
                Assert.That(File.GetLastWriteTimeUtc(path), Is.EqualTo(originalTime));
                Assert.Throws<IOException>(() => file.Read(3, CancellationToken.None));
                Assert.Throws<IOException>(() => file.Write(new byte[] { 7 }));
            }
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(new byte[] { 4, 5, 6 }));
        }

        [TestCase("bounded")]
        [TestCase("manifest")]
        [TestCase("package")]
        [TestCase("meta")]
        [TestCase("name")]
        [TestCase("gitmodules")]
        [TestCase("writable")]
        public void NamedPipe_AllAuthoritativeReadersRejectInBoundedSubprocess(string reader)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                Assert.Ignore("POSIX FIFO filesystem entries are not available on Windows.");
            string path = Path.Combine(directory, Guid.NewGuid().ToString("N"));
            CommandResult created = CliCommandRunner.Run("/usr/bin/mkfifo", "-- " + GitUtility.Quote(path), directory, 5000);
            Assert.That(created.IsSuccess, Is.True, created.StdErr);
            AssertIsolatedRejection(path, reader);
        }

        [TestCase("link")]
        [TestCase("device")]
        public void NativeReader_RejectsLinksAndDevicesInBoundedSubprocess(string kind)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                Assert.Ignore("This test requires unprivileged POSIX links and devices.");
            string path = "/dev/zero";
            if (kind == "link")
            {
                string target = Path.Combine(directory, Guid.NewGuid().ToString("N"));
                path = target + ".link";
                File.WriteAllText(target, "preserve me");
                CommandResult linked = CliCommandRunner.Run("/bin/ln", "-s -- " + GitUtility.Quote(target) + " " + GitUtility.Quote(path), directory, 5000);
                Assert.That(linked.IsSuccess, Is.True, linked.StdErr);
            }
            AssertIsolatedRejection(path, "bounded");
        }

        private void AssertIsolatedRejection(string path, string reader)
        {
            CommandResult result = RunIsolatedReader(path, reader);
            Assert.That(result.IsSuccess, Is.True, result.StdOut + result.StdErr);
            Assert.That(result.TerminationConfirmed, Is.True);
            Assert.That(result.StdOut, Does.Contain("rejected"));
        }

        private CommandResult RunIsolatedReader(string path, string reader)
        {
            return CliCommandRunner.Run(runtimeExecutable,
                new[] { executable, packageAssemblyPath, path, reader, engineAssemblyDirectory },
                directory, 5000);
        }

        private static string GetAssemblyPath(Assembly assembly)
        {
#if UNITY_7000_0_OR_NEWER
            return UnityEngine.AssemblyExtension.GetLoadedAssemblyPath(assembly);
#else
            return assembly.Location;
#endif
        }

        private const string ReaderSource = @"
using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
class Reader {
 [DllImport(""libc"", EntryPoint=""open"", SetLastError=true)]
 static extern int OpenForWrite(string path, int flags);
 [DllImport(""libc"", EntryPoint=""write"", SetLastError=true)]
 static extern IntPtr WriteBytes(int descriptor, byte[] bytes, UIntPtr count);
 [DllImport(""libc"", EntryPoint=""close"", SetLastError=true)]
 static extern int Close(int descriptor);
 static int Main(string[] args) {
  if (args[2]==""edit"") {
   DateTime time=File.GetLastWriteTimeUtc(args[1]);
   // An independent POSIX writer need not honor managed advisory sharing locks.
   int descriptor=OpenForWrite(args[1],1);
   if (descriptor<0) throw new IOException(""Native open failed: ""+Marshal.GetLastWin32Error());
   try {
    if (WriteBytes(descriptor,new byte[]{4,5,6},new UIntPtr(3)).ToInt64()!=3)
     throw new IOException(""Native write failed: ""+Marshal.GetLastWin32Error());
   } finally {
    if (Close(descriptor)!=0) throw new IOException(""Native close failed: ""+Marshal.GetLastWin32Error());
   }
   File.SetLastWriteTimeUtc(args[1],time);
   return 0;
  }
  AppDomain.CurrentDomain.AssemblyResolve += (sender, request) => {
   string name = new AssemblyName(request.Name).Name + "".dll"";
   foreach (string dir in new[] { Path.GetDirectoryName(args[0]), args[3] }) {
    string file = Path.Combine(dir, name);
    if (File.Exists(file)) return Assembly.LoadFrom(file);
   }
   return null;
  };
  Assembly assembly = Assembly.LoadFrom(args[0]);
  string type = ""GitUtility"", method;
  object[] input;
  switch(args[2]) {
   case ""bounded"": type=""BoundedRegularFile""; method=""TryRead""; input=new object[]{args[1],1048576,null,null,CancellationToken.None}; break;
   case ""manifest"": type=""PackageManifestGitDependencyStore""; method=""TryReadRawBytes""; input=new object[]{args[1],null,null}; break;
   case ""package"": method=""TryReadPackageManifestMetadata""; input=new object[]{args[1],null,null,CancellationToken.None}; break;
   case ""meta"": method=""TryReadValidPackageManifestMeta""; input=new object[]{args[1],null,null,CancellationToken.None}; break;
   case ""name"": method=""TryReadPackageName""; input=new object[]{args[1],null}; break;
   case ""gitmodules"": method=""TryReadBoundedGitModulesBytes""; input=new object[]{args[1],null,null,CancellationToken.None}; break;
   case ""writable"": type=""BoundedRegularFile""; method=""Open""; input=new object[]{args[1],true}; break;
   default: return 3;
  }
  MethodInfo target=assembly.GetType(""MartinCalander.GitSubmoduleManager.Editor.""+type,true).GetMethod(method,BindingFlags.Static|BindingFlags.NonPublic);
  try {
   object result=target.Invoke(null,input);
   if (result is IDisposable) ((IDisposable)result).Dispose();
   bool accepted=!(result is bool) || (bool)result;
   Console.WriteLine(accepted ? ""accepted"" : ""rejected"");
   return accepted ? 1 : 0;
  } catch(TargetInvocationException exception) {
   if (args[2]!=""writable"" || !(exception.InnerException is IOException)) throw;
   Console.WriteLine(""rejected""); return 0;
  }
 }
}";
    }
}
