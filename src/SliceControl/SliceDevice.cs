using SliceControl.Hid;

namespace SliceControl;

public sealed class SliceDevice
{
    private const string VidPid = "VID_03F0&PID_0D66";

    public SliceDevicePaths Paths { get; }

    public SliceLights Lights { get; }

    public SliceRaw Raw { get; }

    private SliceDevice(SliceDevicePaths paths)
    {
        Paths = paths;

        Raw = new SliceRaw(paths);
        Lights = new SliceLights(Raw);
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

        return new SliceDevicePaths(
            col01,
            col02,
            col03,
            col04);
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

            EmitChanges(
                reportId: 0x32,
                previous,
                current,
                TelephonyButtons,
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

            EmitChanges(
                reportId: 0x31,
                previous,
                current,
                ConsumerButtons,
                callback);

            previous = current;
        }
    }

    private static void EmitChanges(
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

            bool pressed =
                (current & mask) != 0;

            callback(
                new SliceButtonEvent(
                    button,
                    pressed,
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
        TelephonyButtons =
            new Dictionary<byte, SliceButton>
            {
                [0x01] = SliceButton.HookSwitch,
                [0x02] = SliceButton.Flash,
                [0x04] = SliceButton.Redial,
                [0x08] = SliceButton.SpeakerPhone,
                [0x10] = SliceButton.PhoneMute,
                [0x20] = SliceButton.Send,
                [0x40] = SliceButton.SpeedDial,
                [0x80] = SliceButton.Button7
            };

    private static readonly IReadOnlyDictionary<byte, SliceButton>
        ConsumerButtons =
            new Dictionary<byte, SliceButton>
            {
                [0x01] = SliceButton.VolumeUp,
                [0x02] = SliceButton.VolumeDown
            };
}