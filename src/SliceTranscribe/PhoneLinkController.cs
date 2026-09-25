using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SliceTranscribe;

internal static class PhoneLinkController
{
    private const uint InputKeyboard = 1;
    private const uint KeyUp = 0x0002;

    private const ushort VkControl = 0x11;
    private const ushort VkShift = 0x10;
    private const ushort VkM = 0x4D;
    private const ushort VkH = 0x48;

    public static bool TryToggleMute()
    {
        return TrySendShortcut(
            VkM,
            "mute/unmute");
    }

    public static bool TryHangUp()
    {
        return TrySendShortcut(
            VkH,
            "hang up");
    }

    private static bool TrySendShortcut(
        ushort key,
        string action)
    {
        IntPtr target =
            FindPhoneLinkWindow();

        if (target == IntPtr.Zero)
        {
            Console.Error.WriteLine(
                "Phone Link window was not found. Open Phone Link first.");

            return false;
        }

        IntPtr previous =
            GetForegroundWindow();

        if (!SetForegroundWindow(
            target))
        {
            Console.Error.WriteLine(
                "Could not focus Phone Link.");

            return false;
        }

        Thread.Sleep(
            120);

        SendKey(
            VkControl,
            keyUp: false);

        SendKey(
            VkShift,
            keyUp: false);

        SendKey(
            key,
            keyUp: false);

        SendKey(
            key,
            keyUp: true);

        SendKey(
            VkShift,
            keyUp: true);

        SendKey(
            VkControl,
            keyUp: true);

        Thread.Sleep(
            120);

        if (previous != IntPtr.Zero &&
            previous != target)
        {
            SetForegroundWindow(
                previous);
        }

        Console.WriteLine(
            $"PHONE LINK -> {action}");

        return true;
    }

    private static IntPtr FindPhoneLinkWindow()
    {
        Process[] processes =
            Process.GetProcessesByName(
                "PhoneExperienceHost");

        foreach (Process process in processes)
        {
            try
            {
                if (process.MainWindowHandle !=
                    IntPtr.Zero)
                {
                    return process.MainWindowHandle;
                }
            }
            finally
            {
                process.Dispose();
            }
        }

        return IntPtr.Zero;
    }

    private static void SendKey(
        ushort virtualKey,
        bool keyUp)
    {
        INPUT input =
            new()
            {
                type =
                    InputKeyboard,
                union =
                    new InputUnion
                    {
                        keyboard =
                            new KEYBDINPUT
                            {
                                wVk =
                                    virtualKey,
                                dwFlags =
                                    keyUp
                                        ? KeyUp
                                        : 0
                            }
                    }
            };

        uint sent =
            SendInput(
                1,
                new[] { input },
                Marshal.SizeOf<INPUT>());

        if (sent != 1)
        {
            throw new InvalidOperationException(
                $"SendInput failed with Win32 error {Marshal.GetLastWin32Error()}.");
        }
    }

    [StructLayout(
        LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion union;
    }

    [StructLayout(
        LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public KEYBDINPUT keyboard;
    }

    [StructLayout(
        LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [DllImport(
        "user32.dll",
        SetLastError = true)]
    private static extern uint SendInput(
        uint cInputs,
        INPUT[] pInputs,
        int cbSize);

    [DllImport(
        "user32.dll")]
    private static extern bool SetForegroundWindow(
        IntPtr hWnd);

    [DllImport(
        "user32.dll")]
    private static extern IntPtr GetForegroundWindow();
}
