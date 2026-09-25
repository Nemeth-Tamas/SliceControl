using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;

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

    public static bool TryToggleMediaPlayback()
    {
        AutomationElement? button =
            FindPhoneLinkElementByAutomationId(
                "PlayPauseButton");

        if (button is null)
        {
            Console.Error.WriteLine(
                "PHONE LINK -> PlayPauseButton was not found.");

            return false;
        }

        try
        {
            if (!button.TryGetCurrentPattern(
                InvokePattern.Pattern,
                out object? patternObject))
            {
                Console.Error.WriteLine(
                    "PHONE LINK -> PlayPauseButton does not expose InvokePattern.");

                return false;
            }

            string label =
                button.Current.Name;

            ((InvokePattern)patternObject).Invoke();

            Console.WriteLine(
                string.IsNullOrWhiteSpace(label)
                    ? "PHONE LINK -> media play/pause"
                    : $"PHONE LINK -> media play/pause ({label})");

            return true;
        }
        catch (ElementNotAvailableException)
        {
            Console.Error.WriteLine(
                "PHONE LINK -> PlayPauseButton disappeared before it could be invoked.");

            return false;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"PHONE LINK -> media control failed: {ex.Message}");

            return false;
        }
    }

    private static AutomationElement?
        FindPhoneLinkElementByAutomationId(
            string automationId)
    {
        Process[] processes =
            Process.GetProcessesByName(
                "PhoneExperienceHost");

        try
        {
            foreach (Process process in processes)
            {
                var processCondition =
                    new PropertyCondition(
                        AutomationElement.ProcessIdProperty,
                        process.Id);

                var idCondition =
                    new PropertyCondition(
                        AutomationElement.AutomationIdProperty,
                        automationId);

                var condition =
                    new AndCondition(
                        processCondition,
                        idCondition);

                AutomationElement? element =
                    AutomationElement.RootElement.FindFirst(
                        TreeScope.Descendants,
                        condition);

                if (element is not null)
                {
                    return element;
                }
            }

            return null;
        }
        finally
        {
            foreach (Process process in processes)
            {
                process.Dispose();
            }
        }
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
