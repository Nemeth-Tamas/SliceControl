using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace SliceControl.Hid;

internal static class HidIo
{
    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;

    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;

    private const uint OPEN_EXISTING = 3;

    private const uint FILE_FLAG_OVERLAPPED = 0x40000000;

    public static FileStream OpenRead(string path)
    {
        return Open(path, FileAccess.Read);
    }

    public static FileStream OpenWrite(string path)
    {
        return Open(path, FileAccess.Write);
    }

    public static FileStream OpenReadWrite(string path)
    {
        return Open(path, FileAccess.ReadWrite);
    }

    private static FileStream Open(string path, FileAccess access)
    {
        uint desiredAccess = 0;

        if ((access & FileAccess.Read) != 0)
        {
            desiredAccess |= GENERIC_READ;
        }

        if ((access & FileAccess.Write) != 0)
        {
            desiredAccess |= GENERIC_WRITE;
        }

        SafeFileHandle handle = CreateFile(
            path,
            desiredAccess,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero,
            OPEN_EXISTING,
            FILE_FLAG_OVERLAPPED,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                $"Could not open HID device: {path}");
        }

        return new FileStream(
            handle,
            access,
            bufferSize: 64,
            isAsync: true);
    }

    [DllImport(
        "kernel32.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);
}