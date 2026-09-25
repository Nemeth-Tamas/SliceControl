using NAudio.CoreAudioApi;

namespace SliceTranscribe;

internal static class MicrophoneSelector
{
    public static void PrintActiveMicrophones()
    {
        using var enumerator =
            new MMDeviceEnumerator();

        string? defaultId =
            TryGetDefaultCommunicationsId(
                enumerator);

        MMDeviceCollection devices =
            enumerator.EnumerateAudioEndPoints(
                DataFlow.Capture,
                DeviceState.Active);

        if (devices.Count == 0)
        {
            Console.WriteLine(
                "No active capture devices were found.");

            return;
        }

        for (int i = 0; i < devices.Count; i++)
        {
            using MMDevice device =
                devices[i];

            bool isDefault =
                string.Equals(
                    device.ID,
                    defaultId,
                    StringComparison.OrdinalIgnoreCase);

            Console.WriteLine(
                $"{i,2}: {device.FriendlyName}" +
                (isDefault
                    ? "  [default communications]"
                    : string.Empty));

            Console.WriteLine(
                $"    {device.ID}");
        }
    }

    public static MMDevice Resolve(
        string? requestedName)
    {
        var enumerator =
            new MMDeviceEnumerator();

        try
        {
            if (string.IsNullOrWhiteSpace(
                requestedName))
            {
                try
                {
                    MMDevice defaultDevice =
                        enumerator.GetDefaultAudioEndpoint(
                            DataFlow.Capture,
                            Role.Communications);

                    enumerator.Dispose();
                    return defaultDevice;
                }
                catch
                {
                    // Fall through to the first active capture endpoint.
                }

                MMDeviceCollection active =
                    enumerator.EnumerateAudioEndPoints(
                        DataFlow.Capture,
                        DeviceState.Active);

                if (active.Count == 0)
                {
                    throw new InvalidOperationException(
                        "No active microphone/capture endpoint was found.");
                }

                string id =
                    active[0].ID;

                MMDevice first =
                    enumerator.GetDevice(id);

                enumerator.Dispose();
                return first;
            }

            MMDeviceCollection devices =
                enumerator.EnumerateAudioEndPoints(
                    DataFlow.Capture,
                    DeviceState.Active);

            List<string> matches =
                devices
                    .Where(device =>
                        device.FriendlyName.Contains(
                            requestedName,
                            StringComparison.OrdinalIgnoreCase) ||
                        device.ID.Contains(
                            requestedName,
                            StringComparison.OrdinalIgnoreCase))
                    .Select(device => device.ID)
                    .Distinct(
                        StringComparer.OrdinalIgnoreCase)
                    .ToList();

            if (matches.Count == 0)
            {
                throw new InvalidOperationException(
                    $"No active microphone matched '{requestedName}'. " +
                    "Run 'SliceTranscribe mics' to list devices.");
            }

            if (matches.Count > 1)
            {
                throw new InvalidOperationException(
                    $"More than one microphone matched '{requestedName}'. " +
                    "Use a more specific name or device ID.");
            }

            MMDevice selected =
                enumerator.GetDevice(
                    matches[0]);

            enumerator.Dispose();
            return selected;
        }
        catch
        {
            enumerator.Dispose();
            throw;
        }
    }

    private static string? TryGetDefaultCommunicationsId(
        MMDeviceEnumerator enumerator)
    {
        try
        {
            using MMDevice device =
                enumerator.GetDefaultAudioEndpoint(
                    DataFlow.Capture,
                    Role.Communications);

            return device.ID;
        }
        catch
        {
            return null;
        }
    }
}
