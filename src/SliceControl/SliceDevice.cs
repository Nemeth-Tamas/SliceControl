using SliceControl.Hid;

namespace SliceControl;

public sealed class SliceDevice
{
    private const string VidPid = "VID_03F0&PID_0D66";

    public SliceDevicePaths Paths { get; }

    public SliceLights Lights { get; }

    public SliceRaw Raw { get; }

    public SliceTelephony Telephony { get; }

    private SliceDevice(SliceDevicePaths paths)
    {
        Paths = paths;

        Raw = new SliceRaw(paths);
        Lights = new SliceLights(Raw);
        Telephony = new SliceTelephony(Raw);
    }

    public static SliceDevice Open()
    {
        return new SliceDevice(Discover());
    }

    public static SliceDevicePaths Discover()
    {
        IReadOnlyList<string> allPaths =
            HidEnumerator.Enumerate();

        string[] slicePaths = allPaths
            .Where(path =>
                path.Contains(
                    VidPid,
                    StringComparison.OrdinalIgnoreCase))
            .ToArray();

        string col01 = FindRequired(slicePaths, 1);
        string col02 = FindRequired(slicePaths, 2);
        string col03 = FindRequired(slicePaths, 3);

        string? col04 = FindOptional(slicePaths, 4);
        string? col05 = FindOptional(slicePaths, 5);

        return new SliceDevicePaths(
            col01,
            col02,
            col03,
            col04,
            col05);
    }

    public async Task WatchButtonsAsync(
        Action<SliceButtonEvent> callback,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callback);

        Task telephonyTask =
            WatchTelephonyAsync(
                callback,
                cancellationToken);

        Task consumerTask =
            WatchConsumerAsync(
                callback,
                cancellationToken);

        await Task.WhenAll(
            telephonyTask,
            consumerTask);
    }

    public async Task WatchRawAsync(
        Action<SliceRawInputReport> callback,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callback);

        var tasks = new List<Task>
        {
            WatchRawCollectionAsync(
                Paths.Collection01,
                1,
                callback,
                cancellationToken),

            WatchRawCollectionAsync(
                Paths.Collection02,
                2,
                callback,
                cancellationToken)
        };

        if (Paths.Collection05 is not null)
        {
            tasks.Add(
                WatchOptionalRawCollectionAsync(
                    Paths.Collection05,
                    5,
                    callback,
                    cancellationToken));
        }

        await Task.WhenAll(tasks);
    }

    private async Task WatchTelephonyAsync(
        Action<SliceButtonEvent> callback,
        CancellationToken cancellationToken)
    {
        using FileStream stream =
            HidIo.OpenRead(Paths.Collection01);

        byte previous = 0;

        byte[] buffer = new byte[2];

        while (!cancellationToken.IsCancellationRequested)
        {
            int read;

            try
            {
                read = await stream.ReadAsync(
                    buffer,
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (read < 2)
            {
                continue;
            }

            if (buffer[0] != 0x32)
            {
                continue;
            }

            byte current = buffer[1];

            EmitAbsoluteChanges(
                reportId: 0x32,
                previous,
                current,
                TelephonyAbsoluteButtons,
                callback);

            EmitTriggers(
                reportId: 0x32,
                current,
                TelephonyTriggerButtons,
                callback);

            previous = current;
        }
    }

    private async Task WatchConsumerAsync(
        Action<SliceButtonEvent> callback,
        CancellationToken cancellationToken)
    {
        using FileStream stream =
            HidIo.OpenRead(Paths.Collection02);

        byte previous = 0;

        byte[] buffer = new byte[2];

        while (!cancellationToken.IsCancellationRequested)
        {
            int read;

            try
            {
                read = await stream.ReadAsync(
                    buffer,
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (read < 2)
            {
                continue;
            }

            if (buffer[0] != 0x31)
            {
                continue;
            }

            byte current = buffer[1];

            EmitAbsoluteChanges(
                reportId: 0x31,
                previous,
                current,
                ConsumerButtons,
                callback);

            previous = current;
        }
    }

    private static async Task WatchOptionalRawCollectionAsync(
        string path,
        int collection,
        Action<SliceRawInputReport> callback,
        CancellationToken cancellationToken)
    {
        try
        {
            await WatchRawCollectionAsync(
                path,
                collection,
                callback,
                cancellationToken);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Some Windows HID keyboard collections are present in enumeration
            // but cannot be opened for generic user-mode reads. Collection 05 is
            // optional reverse-engineering input, so do not terminate monitoring
            // of the confirmed telephony/consumer collections when this occurs.
        }
    }

    private static async Task WatchRawCollectionAsync(
        string path,
        int collection,
        Action<SliceRawInputReport> callback,
        CancellationToken cancellationToken)
    {
        using FileStream stream =
            HidIo.OpenRead(path);

        byte[] buffer = new byte[64];

        while (!cancellationToken.IsCancellationRequested)
        {
            int read;

            try
            {
                read = await stream.ReadAsync(
                    buffer,
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (read <= 0)
            {
                continue;
            }

            byte[] report =
                buffer.Take(read).ToArray();

            callback(
                new SliceRawInputReport(
                    collection,
                    report));
        }
    }

    private static void EmitAbsoluteChanges(
        byte reportId,
        byte previous,
        byte current,
        IReadOnlyDictionary<byte, SliceButton> map,
        Action<SliceButtonEvent> callback)
    {
        byte changed =
            (byte)(previous ^ current);

        foreach ((byte mask, SliceButton button) in map)
        {
            if ((changed & mask) == 0)
            {
                continue;
            }

            bool active =
                (current & mask) != 0;

            callback(
                new SliceButtonEvent(
                    button,
                    active
                        ? SliceButtonEventKind.Down
                        : SliceButtonEventKind.Up,
                    reportId,
                    current));
        }
    }

    private static void EmitTriggers(
        byte reportId,
        byte current,
        IReadOnlyDictionary<byte, SliceButton> map,
        Action<SliceButtonEvent> callback)
    {
        foreach ((byte mask, SliceButton button) in map)
        {
            if ((current & mask) == 0)
            {
                continue;
            }

            callback(
                new SliceButtonEvent(
                    button,
                    SliceButtonEventKind.Triggered,
                    reportId,
                    current));
        }
    }

    private static string FindRequired(
        IEnumerable<string> paths,
        int collection)
    {
        return FindOptional(paths, collection)
            ?? throw new InvalidOperationException(
                $"HP Slice HID Collection {collection:00} was not found.");
    }

    private static string? FindOptional(
        IEnumerable<string> paths,
        int collection)
    {
        string marker =
            $"&COL{collection:00}#";

        return paths.FirstOrDefault(
            path =>
                path.Contains(
                    marker,
                    StringComparison.OrdinalIgnoreCase));
    }

    private static readonly IReadOnlyDictionary<byte, SliceButton>
        TelephonyAbsoluteButtons =
            new Dictionary<byte, SliceButton>
            {
                [0x01] = SliceButton.HookSwitch,
                [0x02] = SliceButton.Flash,
                [0x08] = SliceButton.SpeakerPhone,
                [0x20] = SliceButton.Send,
                [0x80] = SliceButton.Button7
            };

    private static readonly IReadOnlyDictionary<byte, SliceButton>
        TelephonyTriggerButtons =
            new Dictionary<byte, SliceButton>
            {
                [0x04] = SliceButton.Redial,
                [0x10] = SliceButton.PhoneMute,
                [0x40] = SliceButton.SpeedDial
            };

    private static readonly IReadOnlyDictionary<byte, SliceButton>
        ConsumerButtons =
            new Dictionary<byte, SliceButton>
            {
                [0x01] = SliceButton.VolumeUp,
                [0x02] = SliceButton.VolumeDown
            };
}
