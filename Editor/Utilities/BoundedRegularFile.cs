using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace MartinCalander.GitSubmoduleManager.Editor
{
    /// <summary>
    /// Reads existing regular files through the handle whose type was checked.
    /// Callers remain responsible for validating ancestor directories and ownership.
    /// </summary>
    internal static class BoundedRegularFile
    {
        internal static bool TryRead(
            string path,
            int maximumBytes,
            out byte[] bytes,
            out string error,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            bytes = Array.Empty<byte>();
            error = string.Empty;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                using (OpenedFile file = Open(path))
                {
                    bytes = file.Read(maximumBytes, cancellationToken);
                    // Reopen without following links to bind the accepted bytes to
                    // the path as well as the still-open original handle.
                    using (OpenedFile current = Open(path))
                    {
                        if (!file.Snapshot.Equals(current.Inspect()))
                            throw new IOException("The file changed identity while it was being read.");
                    }
                }
                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                bytes = Array.Empty<byte>();
                error = "The entry is not a safely readable regular file: " + exception.Message;
                return false;
            }
        }

        internal static OpenedFile Open(string path, bool writable = false)
        {
            return new OpenedFile(path, writable);
        }

        internal struct FileSnapshot : IEquatable<FileSnapshot>
        {
            internal ulong Device;
            internal ulong Identity;
            internal long Length;
            internal long Modified;
            internal long ModifiedFraction;
            internal long Changed;
            internal long ChangedFraction;

            public bool Equals(FileSnapshot other)
            {
                return Device == other.Device && Identity == other.Identity &&
                       Length == other.Length && Modified == other.Modified &&
                       ModifiedFraction == other.ModifiedFraction &&
                       Changed == other.Changed && ChangedFraction == other.ChangedFraction;
            }
        }

        internal sealed class OpenedFile : IDisposable
        {
            private readonly bool windows;
            private readonly bool darwin;
            private readonly bool writable;
            private SafeFileHandle handle;
            private FileStream exclusiveStream;
            private int descriptor = -1;
            internal FileSnapshot Snapshot { get; private set; }

            internal OpenedFile(string path, bool writable)
            {
                this.writable = writable;
                windows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
                darwin = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
                try
                {
                    if (windows)
                    {
                        // OPEN_EXISTING and OPEN_REPARSE_POINT never create a file
                        // or follow the final component's link. Deny other writers.
                        handle = CreateFile(path, writable ? 0xc0000000u : 0x80000000u,
                            writable ? 0u : 1u, IntPtr.Zero, 3, 0x00200000u, IntPtr.Zero);
                        if (handle.IsInvalid)
                            throw NativeError("Could not open the file without following links");
                    }
                    else
                    {
                        if (IntPtr.Size != 8 ||
                            (!darwin && (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ||
                             RuntimeInformation.ProcessArchitecture != Architecture.X64)))
                        {
                            throw new PlatformNotSupportedException(
                                "Regular-file inspection requires a supported 64-bit Editor host.");
                        }

                        // O_NONBLOCK rejects pipes before any blocking read;
                        // O_NOFOLLOW rejects a late link; O_CLOEXEC keeps the
                        // handle out of concurrently launched Git processes.
                        int flags = darwin ? 0x01000104 : 0x000a0800;
                        descriptor = OpenUnix(path, flags | (writable ? 2 : 0));
                        if (descriptor < 0)
                            throw NativeError("Could not open the file without following links");
                    }

                    Snapshot = Inspect();
                    if (!windows && writable)
                    {
                        // Mono owns a separate sharing registry. Open its stream
                        // through the validated descriptor, never the mutable
                        // repository path, to preserve FileShare.None without
                        // risking a blocking FIFO open after the type check.
                        exclusiveStream = new FileStream(
                            "/dev/fd/" + descriptor,
                            FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                        if (!Snapshot.Equals(Inspect()))
                            throw new IOException("The file changed while its exclusive stream was opened.");
                    }
                }
                catch
                {
                    Dispose();
                    throw;
                }
            }

            internal FileSnapshot Inspect()
            {
                if (windows)
                {
                    if (handle == null || handle.IsClosed ||
                        GetFileType(handle) != 1 ||
                        !GetFileInformation(handle, out WindowsFileInformation info))
                    {
                        throw NativeError("The entry must be a regular disk file");
                    }
                    if ((info.Attributes & (0x10u | 0x400u)) != 0)
                        throw new IOException("The entry must be a regular file, not a directory or symbolic link.");
                    return new FileSnapshot
                    {
                        Device = info.Volume,
                        Identity = ((ulong)info.IndexHigh << 32) | info.IndexLow,
                        Length = checked((long)(((ulong)info.SizeHigh << 32) | info.SizeLow)),
                        Modified = info.WriteTime,
                        Changed = info.CreationTime
                    };
                }

                if (descriptor < 0)
                    throw new ObjectDisposedException(nameof(OpenedFile));
                // Darwin's stat64 ABI is shared by x64 and arm64. Linux Editors
                // use the x86-64 stat ABI. Oversized storage protects the native
                // call; only documented identity, type, length and time fields
                // are decoded. Access time is deliberately excluded.
                var data = new byte[256];
                int result = darwin
                    ? StatDarwin(descriptor, data)
                    : StatLinux(descriptor, data);
                if (result != 0)
                    throw NativeError("Could not inspect the opened file");
                uint mode = darwin ? BitConverter.ToUInt16(data, 4) : BitConverter.ToUInt32(data, 24);
                if ((mode & 0xf000u) != 0x8000u)
                    throw new IOException("The entry must be a regular file, not a pipe, device, directory, or symbolic link.");
                return new FileSnapshot
                {
                    Device = darwin ? BitConverter.ToUInt32(data, 0) : BitConverter.ToUInt64(data, 0),
                    Identity = BitConverter.ToUInt64(data, 8),
                    Length = BitConverter.ToInt64(data, darwin ? 96 : 48),
                    Modified = BitConverter.ToInt64(data, darwin ? 48 : 88),
                    ModifiedFraction = BitConverter.ToInt64(data, darwin ? 56 : 96),
                    Changed = BitConverter.ToInt64(data, darwin ? 64 : 104),
                    ChangedFraction = BitConverter.ToInt64(data, darwin ? 72 : 112)
                };
            }

            internal byte[] Read(int maximumBytes, CancellationToken cancellationToken)
            {
                if (maximumBytes < 0)
                    throw new ArgumentOutOfRangeException(nameof(maximumBytes));
                FileSnapshot before = Inspect();
                if (!Snapshot.Equals(before))
                    throw new IOException("The file changed before it could be read.");
                if (before.Length < 0 || before.Length > maximumBytes)
                    throw new IOException("The file exceeds the safety size limit.");
                var bytes = new byte[(int)before.Length];
                var chunk = new byte[8192];
                int offset = 0;
                SeekToStart();
                while (offset < bytes.Length)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int count = ReadChunk(chunk, Math.Min(chunk.Length, bytes.Length - offset));
                    if (count == 0)
                        throw new IOException("The file changed length while it was being read.");
                    Buffer.BlockCopy(chunk, 0, bytes, offset, count);
                    offset += count;
                }
                cancellationToken.ThrowIfCancellationRequested();
                if (ReadChunk(chunk, 1) != 0 || !before.Equals(Inspect()))
                    throw new IOException("The file changed while it was being read.");
                return bytes;
            }

            internal void Write(byte[] bytes)
            {
                if (!writable)
                    throw new InvalidOperationException("This file was opened for reading only.");
                if (!Snapshot.Equals(Inspect()))
                    throw new IOException("The file changed before its verified replacement.");
                if (exclusiveStream != null)
                {
                    exclusiveStream.Position = 0;
                    exclusiveStream.SetLength(0);
                    exclusiveStream.Write(bytes, 0, bytes.Length);
                    exclusiveStream.Flush(true);
                    Snapshot = Inspect();
                    return;
                }
                SeekToStart();
                int offset = 0;
                var chunk = new byte[8192];
                while (offset < bytes.Length)
                {
                    int count = Math.Min(chunk.Length, bytes.Length - offset);
                    Buffer.BlockCopy(bytes, offset, chunk, 0, count);
                    if (!WriteWindows(handle, chunk, count, out int written, IntPtr.Zero))
                        throw NativeError("Could not write the verified file");
                    if (written <= 0)
                        throw NativeError("Could not write the verified file");
                    offset += written;
                }
                if (!SetEndOfFile(handle) || !FlushWindows(handle))
                    throw NativeError("Could not finalize the verified file");
                Snapshot = Inspect();
            }

            private int ReadChunk(byte[] bytes, int count)
            {
                if (exclusiveStream != null)
                    return exclusiveStream.Read(bytes, 0, count);
                if (windows)
                {
                    if (!ReadWindows(handle, bytes, count, out int read, IntPtr.Zero))
                        throw NativeError("Could not read the regular file");
                    return read;
                }
                long result;
                do { result = ReadUnix(descriptor, bytes, new UIntPtr((uint)count)).ToInt64(); }
                while (result < 0 && Marshal.GetLastWin32Error() == 4);
                if (result < 0)
                    throw NativeError("Could not read the regular file");
                return checked((int)result);
            }

            private void SeekToStart()
            {
                if (exclusiveStream != null)
                {
                    exclusiveStream.Position = 0;
                    return;
                }
                if (windows ? !SeekWindows(handle, 0, out _, 0) : SeekUnix(descriptor, 0, 0) < 0)
                    throw NativeError("The entry must be a seekable regular file");
            }

            public void Dispose()
            {
                try
                {
                    exclusiveStream?.Dispose();
                }
                finally
                {
                    handle?.Dispose();
                    if (descriptor >= 0)
                    {
                        int owned = descriptor;
                        descriptor = -1;
                        CloseUnix(owned);
                    }
                }
            }
        }

        private static IOException NativeError(string message)
        {
            int code = Marshal.GetLastWin32Error();
            string diagnostic = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? new Win32Exception(code).Message
                : Marshal.PtrToStringAnsi(DescribeUnixError(code));
            return new IOException(message + ": " + diagnostic);
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct WindowsFileInformation
        {
            internal uint Attributes;
            internal long CreationTime;
            internal long AccessTime;
            internal long WriteTime;
            internal uint Volume;
            internal uint SizeHigh;
            internal uint SizeLow;
            internal uint Links;
            internal uint IndexHigh;
            internal uint IndexLow;
        }

        [DllImport("libc", EntryPoint = "open", SetLastError = true)]
        private static extern int OpenUnix(string path, int flags);
        [DllImport("libc", EntryPoint = "fstat64", SetLastError = true)]
        private static extern int StatDarwin(int descriptor, [Out] byte[] stat);
        [DllImport("libc", EntryPoint = "fstat", SetLastError = true)]
        private static extern int StatLinux(int descriptor, [Out] byte[] stat);
        [DllImport("libc", EntryPoint = "read", SetLastError = true)]
        private static extern IntPtr ReadUnix(int descriptor, [Out] byte[] bytes, UIntPtr count);
        [DllImport("libc", EntryPoint = "lseek", SetLastError = true)]
        private static extern long SeekUnix(int descriptor, long offset, int origin);
        [DllImport("libc", EntryPoint = "close", SetLastError = true)]
        private static extern int CloseUnix(int descriptor);
        [DllImport("libc", EntryPoint = "strerror")]
        private static extern IntPtr DescribeUnixError(int code);
        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint mode, uint flags, IntPtr template);
        [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandle", SetLastError = true)]
        private static extern bool GetFileInformation(SafeFileHandle handle, out WindowsFileInformation info);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint GetFileType(SafeFileHandle handle);
        [DllImport("kernel32.dll", EntryPoint = "ReadFile", SetLastError = true)]
        private static extern bool ReadWindows(SafeFileHandle handle, [Out] byte[] bytes, int count, out int read, IntPtr overlapped);
        [DllImport("kernel32.dll", EntryPoint = "WriteFile", SetLastError = true)]
        private static extern bool WriteWindows(SafeFileHandle handle, byte[] bytes, int count, out int written, IntPtr overlapped);
        [DllImport("kernel32.dll", EntryPoint = "SetFilePointerEx", SetLastError = true)]
        private static extern bool SeekWindows(SafeFileHandle handle, long offset, out long position, uint origin);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetEndOfFile(SafeFileHandle handle);
        [DllImport("kernel32.dll", EntryPoint = "FlushFileBuffers", SetLastError = true)]
        private static extern bool FlushWindows(SafeFileHandle handle);
    }
}
