using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace SliceControl;

/// <summary>
/// Persistent session for the private control device exposed by
/// HPSliceTelephony.sys.
/// </summary>
/// <remarks>
/// The HP private PDO keeps state per open handle. In particular, volume
/// notifications only behave like the stock service after a key-press event
/// has been registered on the same handle.
/// </remarks>
public sealed class SlicePrivateDriver : IDisposable
{
    public const string DevicePath = @"\\.\HPSlicePDO_SYM_03F0";

    public const uint IoctlVolumeChangeNotification = 0x3C4A2004;
    public const uint IoctlKeyPressEventRegister = 0x3C4A2008;
    public const uint IoctlKeyPressEventDeregister = 0x3C4A200C;
    public const uint IoctlSendHello = 0x3C4A2010;
    public const uint IoctlSendGoodbye = 0x3C4A2014;

    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;

    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;

    private const uint OpenExisting = 3;
    private const uint FileFlagOverlapped = 0x40000000;

    private readonly SafeFileHandle _handle;
    private readonly EventWaitHandle _keyEvent;

    private bool _registered;
    private bool _disposed;

    private SlicePrivateDriver(
        SafeFileHandle handle,
        EventWaitHandle keyEvent)
    {
        _handle = handle;
        _keyEvent = keyEvent;
    }

    public static SlicePrivateDriver Open()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "The HP Slice private driver is available only on Windows.");
        }

        SafeFileHandle handle = CreateFile(
            DevicePath,
            GenericRead | GenericWrite,
            FileShareRead | FileShareWrite | FileShareDelete,
            IntPtr.Zero,
            OpenExisting,
            FileFlagOverlapped,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                $"Could not open HP Slice private device: {DevicePath}");
        }

        var keyEvent =
            new EventWaitHandle(
                initialState: false,
                EventResetMode.AutoReset);

        var session =
            new SlicePrivateDriver(
                handle,
                keyEvent);

        try
        {
            session.RegisterKeyPressEvent();
            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Waits for the HP driver to signal that it recognized a Collaboration
    /// Cover key press.
    /// </summary>
    public async Task WaitForKeyPressAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        await Task.Run(
            () =>
            {
                int index = WaitHandle.WaitAny(
                    new WaitHandle[]
                    {
                        _keyEvent,
                        cancellationToken.WaitHandle
                    });

                if (index == 1)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
            },
            CancellationToken.None);
    }

    /// <summary>
    /// Sends the stock driver's volume-change notification. The value is the
    /// Windows master-volume percentage expected by the HP service.
    /// </summary>
    public void SetVolume(byte value)
    {
        ThrowIfDisposed();

        SendIoctl(
            IoctlVolumeChangeNotification,
            new[] { value });
    }

    public void Hello()
    {
        ThrowIfDisposed();
        SendIoctl(IoctlSendHello);
    }

    public void Goodbye()
    {
        ThrowIfDisposed();
        SendIoctl(IoctlSendGoodbye);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_registered && !_handle.IsInvalid && !_handle.IsClosed)
        {
            try
            {
                SendIoctlCore(
                    IoctlKeyPressEventDeregister,
                    null);
            }
            catch
            {
                // Dispose must still release the local handles if the device
                // disappeared or the driver is shutting down.
            }

            _registered = false;
        }

        _keyEvent.Dispose();
        _handle.Dispose();
    }

    private void RegisterKeyPressEvent()
    {
        IntPtr eventHandle =
            _keyEvent.SafeWaitHandle.DangerousGetHandle();

        byte[] handleBytes =
            IntPtr.Size == 8
                ? BitConverter.GetBytes(eventHandle.ToInt64())
                : BitConverter.GetBytes(eventHandle.ToInt32());

        SendIoctl(
            IoctlKeyPressEventRegister,
            handleBytes);

        _registered = true;
    }

    private void SendIoctl(
        uint code,
        byte[]? input = null)
    {
        if (!SendIoctlCore(code, input))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                $"HP Slice private IOCTL 0x{code:X8} failed.");
        }
    }

    private bool SendIoctlCore(
        uint code,
        byte[]? input)
    {
        return DeviceIoControl(
            _handle,
            code,
            input,
            input?.Length ?? 0,
            IntPtr.Zero,
            0,
            out _,
            IntPtr.Zero);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(
            _disposed,
            this);
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

    [DllImport(
        "kernel32.dll",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        [In] byte[]? lpInBuffer,
        int nInBufferSize,
        IntPtr lpOutBuffer,
        int nOutBufferSize,
        out int lpBytesReturned,
        IntPtr lpOverlapped);
}
