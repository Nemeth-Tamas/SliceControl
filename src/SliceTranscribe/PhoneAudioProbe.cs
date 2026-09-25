using Windows.Devices.Enumeration;
using Windows.Media.Audio;

namespace SliceTranscribe;

internal static class PhoneAudioProbe
{
    public static async Task<int> ListAsync()
    {
        string selector =
            AudioPlaybackConnection.GetDeviceSelector();

        DeviceInformationCollection devices =
            await DeviceInformation.FindAllAsync(
                selector);

        if (devices.Count == 0)
        {
            Console.WriteLine(
                "No paired A2DP source devices were found.");

            Console.WriteLine(
                "Pair the phone in Windows Bluetooth settings, then run this command again.");

            return 1;
        }

        Console.WriteLine(
            "A2DP source devices:");

        for (int i = 0; i < devices.Count; i++)
        {
            DeviceInformation device =
                devices[i];

            Console.WriteLine(
                $"  [{i}] {device.Name}");

            Console.WriteLine(
                $"      {device.Id}");
        }

        return 0;
    }

    public static async Task<int> ConnectAsync(
        string? requestedName,
        CancellationToken cancellationToken)
    {
        string selector =
            AudioPlaybackConnection.GetDeviceSelector();

        DeviceInformationCollection devices =
            await DeviceInformation.FindAllAsync(
                selector);

        if (devices.Count == 0)
        {
            Console.WriteLine(
                "No paired A2DP source devices were found.");

            return 1;
        }

        DeviceInformation? selected =
            null;

        if (!string.IsNullOrWhiteSpace(
            requestedName))
        {
            selected =
                devices.FirstOrDefault(
                    device =>
                        device.Name.Contains(
                            requestedName,
                            StringComparison.OrdinalIgnoreCase));
        }
        else if (devices.Count == 1)
        {
            selected =
                devices[0];
        }

        if (selected is null)
        {
            Console.WriteLine(
                "Multiple devices are available. Use:");

            Console.WriteLine(
                "  SliceTranscribe phone connect --name \"device name\"");

            Console.WriteLine();

            await ListAsync();

            return 2;
        }

        using AudioPlaybackConnection? connection =
            AudioPlaybackConnection.TryCreateFromId(
                selected.Id);

        if (connection is null)
        {
            Console.Error.WriteLine(
                $"Could not create an A2DP sink connection for {selected.Name}.");

            return 1;
        }

        connection.StateChanged +=
            (_, _) =>
            {
                Console.WriteLine(
                    $"PHONE STATE -> {connection.State}");
            };

        Console.WriteLine(
            $"PHONE -> enabling {selected.Name}");

        await connection.StartAsync();

        Console.WriteLine(
            $"PHONE -> opening {selected.Name}");

        AudioPlaybackConnectionOpenResult result =
            await connection.OpenAsync();

        Console.WriteLine(
            $"PHONE OPEN -> {result.Status}");

        if (result.Status !=
            AudioPlaybackConnectionOpenResultStatus.Success)
        {
            return 1;
        }

        Console.WriteLine();
        Console.WriteLine(
            "Phone audio sink is OPEN.");

        Console.WriteLine(
            "Start music on the phone; Windows should play it through the current default output.");

        Console.WriteLine(
            "Press Ctrl+C here to release the phone connection.");

        var completion =
            new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);

        ConsoleCancelEventHandler handler =
            (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                completion.TrySetResult(
                    true);
            };

        Console.CancelKeyPress +=
            handler;

        try
        {
            using CancellationTokenRegistration registration =
                cancellationToken.Register(
                    () =>
                        completion.TrySetCanceled(
                            cancellationToken));

            await completion.Task;
        }
        finally
        {
            Console.CancelKeyPress -=
                handler;
        }

        return 0;
    }
}
