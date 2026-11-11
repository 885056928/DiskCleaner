using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DiskCleaner.Services;

internal readonly record struct FileSnapshot(
    uint Volume, ulong Id, long Created, long Modified, long Size)
{
    private const uint ReadAttributes = 0x80;
    private const uint ListDirectory = 0x1;
    private const uint DeleteAccess = 0x10000;
    private const uint OpenExisting = 3;
    private const uint OpenReparsePoint = 0x00200000;
    private const uint BackupSemantics = 0x02000000;
    private const int FileDispositionInfo = 4;

    internal const FileAttributes UnsafeAttributes = FileAttributes.ReparsePoint | FileAttributes.Offline;

    internal static FileSnapshot? Read(string path)
    {
        using var handle = Open(path, ReadAttributes, FileShare.ReadWrite | FileShare.Delete);
        return Read(handle);
    }

    internal bool DeleteIfUnchanged(string path)
    {
        var directories = new Stack<SafeFileHandle>();
        try
        {
            // 逐级保留目录句柄，避免检查后目录被改名或替换为指向其他位置的链接。
            var parent = Path.GetDirectoryName(path)!;
            var drive = Path.GetPathRoot(parent)!;
            var current = drive;
            foreach (var name in parent[drive.Length..].Split(Path.DirectorySeparatorChar,
                StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, name);
                // 只有读属性权限的句柄不会参与 Windows 的共享访问检查。
                var directory = Open(current, ListDirectory | ReadAttributes, FileShare.Read, BackupSemantics);
                directories.Push(directory);
                if (directory.IsInvalid || !GetFileInformationByHandle(directory, out var info)
                    || (info.Attributes & FileAttributes.Directory) == 0
                    || (info.Attributes & UnsafeAttributes) != 0)
                {
                    return false;
                }
            }

            // 以独占方式复核文件身份和状态，并在同一个句柄上标记删除。
            using var file = Open(path, ReadAttributes | DeleteAccess, FileShare.None);
            byte deleteFile = 1;
            return Read(file) == this
                && SetFileInformationByHandle(file, FileDispositionInfo, ref deleteFile, sizeof(byte));
        }
        finally
        {
            while (directories.TryPop(out var directory))
            {
                directory.Dispose();
            }
        }
    }

    private static FileSnapshot? Read(SafeFileHandle handle)
    {
        const FileAttributes protectedAttributes = UnsafeAttributes | FileAttributes.Directory
            | FileAttributes.System | FileAttributes.ReadOnly;
        if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var info)
            || (info.Attributes & protectedAttributes) != 0 || info.Links != 1)
        {
            return null;
        }

        return new FileSnapshot(info.Volume, (ulong)info.IdHigh << 32 | info.IdLow,
            info.Created.Value, info.Modified.Value, (long)info.SizeHigh << 32 | info.SizeLow);
    }

    private static SafeFileHandle Open(string path, uint access, FileShare sharing, uint flags = 0)
    {
        // 长路径仍需保持 Win32 的逐级链接检查。
        var extendedPath = path.StartsWith(@"\\", StringComparison.Ordinal)
            ? @"\\?\UNC\" + path[2..]
            : @"\\?\" + path;
        return CreateFileW(extendedPath, access, sharing, IntPtr.Zero, OpenExisting,
            flags | OpenReparsePoint, IntPtr.Zero);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        internal uint Low;
        internal uint High;
        internal readonly long Value => (long)High << 32 | Low;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        internal FileAttributes Attributes;
        internal FileTime Created;
        internal FileTime Accessed;
        internal FileTime Modified;
        internal uint Volume;
        internal uint SizeHigh;
        internal uint SizeLow;
        internal uint Links;
        internal uint IdHigh;
        internal uint IdLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, FileShare share,
        IntPtr securityAttributes, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation info);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int informationClass,
        ref byte info, uint size);
}
