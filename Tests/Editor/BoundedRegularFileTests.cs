using System;
using System.IO;
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
        private string mono;
        private string executable;

        [OneTimeSetUp]
        public void CreateIsolatedReader()
        {
            directory = Path.Combine(Path.GetTempPath(), "GitSubmoduleRegularFileTests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string contents = EditorApplication.applicationContentsPath;
            string runtime = Path.Combine(contents, "Resources", "Scripting", "MonoBleedingEdge");
            if (!Directory.Exists(runtime))
                runtime = Path.Combine(contents, "MonoBleedingEdge");
            mono = Path.Combine(runtime, "bin", RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "mono.exe" : "mono");
            string compiler = Path.Combine(runtime, "lib", "mono", "4.5", "mcs.exe");
            Assert.That(File.Exists(mono) && File.Exists(compiler), Is.True,
                "The Editor's bundled Mono/compiler must be discovered before running isolated filesystem regressions.");
            string source = Path.Combine(directory, "Reader.cs");
            executable = Path.Combine(directory, "Reader.exe");
            File.WriteAllText(source, ReaderSource);
            CommandResult compiled = CliCommandRunner.Run(mono,
                GitUtility.Quote(compiler) + " -out:" + GitUtility.Quote(executable) + " " + GitUtility.Quote(source),
                directory, 20000);
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
            using (BoundedRegularFile.OpenedFile file = BoundedRegularFile.Open(path, writable: true))
            {
                CommandResult changed = RunIsolatedReader(path, "edit");
                Assert.That(changed.IsSuccess, Is.True, changed.StdOut + changed.StdErr);
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
            return CliCommandRunner.Run(mono,
                GitUtility.Quote(executable) + " " + GitUtility.Quote(typeof(GitUtility).Assembly.Location) +
                " " + GitUtility.Quote(path) + " " + reader +
                " " + GitUtility.Quote(Path.GetDirectoryName(typeof(UnityEngine.Object).Assembly.Location)),
                directory, 5000);
        }

        private const string ReaderSource = @"
using System;
using System.IO;
using System.Reflection;
using System.Threading;
class Reader {
 static int Main(string[] args) {
  if (args[2]==""edit"") {
   DateTime time=File.GetLastWriteTimeUtc(args[1]);
   File.WriteAllBytes(args[1],new byte[]{4,5,6});
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
