using Windows.Devices.Enumeration;
using Windows.Media.Audio;

namespace SliceTranscribe;

internal sealed class PhoneAudioManager :
    IAsyncDisposable
{
    private readonly string? _preferredName;
    private readonly TimeSpan _retryDelay =
        TimeSpan.FromSeconds(5);

    private AudioPlaybackConnection? _connection;
    private string? _connectedDeviceName;

    public PhoneAudioManager(
        string? preferredName)
    {
        _preferredName =
            string.IsNullOrWhiteSpace(preferredName)
                ? null
                : preferredName;
    }

    public bool IsConnected =>
        _connection is not null;

    public string? ConnectedDeviceName =>
        _connectedDeviceName;

    public async Task RunAsync(
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                DeviceInformation? device =
                    await FindPreferredDeviceAsync();

                if (device is null)
                {
                    Console.WriteLine(
                        _preferredName is null
                            ? "PHONE -> waiting for a paired A2DP source"
                            : $"PHONE -> waiting for {_preferredName}");

                    await Task.Delay(
                        _retryDelay,
                        cancellationToken);

                    continue;
                }

                AudioPlaybackConnection? connection =
                    AudioPlaybackConnection.TryCreateFromId(
                        device.Id);

                if (connection is null)
                {
                    Console.WriteLine(
                        $"PHONE -> could not create sink for {device.Name}");

                    await Task.Delay(
                        _retryDelay,
                        cancellationToken);

                    continue;
                }

                _connection =
                    connection;

                _connectedDeviceName =
                    device.Name;

                var closed =
                    new TaskCompletionSource<bool>(
                        TaskCreationOptions.RunContinuationsAsynchronously);

                void StateChanged(
                    AudioPlaybackConnection sender,
                    object args)
                {
                    Console.WriteLine(
                        $"PHONE STATE -> {sender.State}");

                    if (sender.State ==
                        AudioPlaybackConnectionState.Closed)
                    {
                        Console.WriteLine(
                            "PHONE -> disconnected; reconnect will be attempted automatically");

                        closed.TrySetResult(
                            true);
                    }
                }

                connection.StateChanged +=
                    StateChanged;

                try
                {
                    Console.WriteLine(
                        $"PHONE -> enabling {device.Name}");

                    await connection.StartAsync();

                    Console.WriteLine(
                        $"PHONE -> opening {device.Name}");

                    AudioPlaybackConnectionOpenResult result =
                        await connection.OpenAsync();

                    Console.WriteLine(
                        $"PHONE OPEN -> {result.Status}");

                    if (result.Status !=
                        AudioPlaybackConnectionOpenResultStatus.Success)
                    {
                        await Task.Delay(
                            _retryDelay,
                            cancellationToken);

                        continue;
                    }

                    // OpenAsync Success is authoritative. Do not immediately
                    // poll State here: after an RF reconnect Windows can lag
                    // briefly before State/StateChanged settles to Opened.
                    // Keep this successful connection alive until Windows
                    // explicitly raises Closed.
                    await closed.Task.WaitAsync(
                        cancellationToken);
                }
                finally
                {
                    connection.StateChanged -=
                        StateChanged;

                    await ReleaseConnectionAsync();
                }
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"PHONE -> connection error: {ex.Message}");

                await ReleaseConnectionAsync();

                try
                {
                    await Task.Delay(
                        _retryDelay,
                        cancellationToken);
                }
                catch (OperationCanceledException)
                    when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await ReleaseConnectionAsync();
    }

    private async Task<DeviceInformation?>
        FindPreferredDeviceAsync()
    {
        DeviceInformationCollection devices =
            await DeviceInformation.FindAllAsync(
                AudioPlaybackConnection.GetDeviceSelector());

        if (devices.Count == 0)
        {
            return null;
        }

        if (_preferredName is not null)
        {
            DeviceInformation? preferred =
                devices.FirstOrDefault(
                    device =>
                        device.Name.Contains(
                            _preferredName,
                            StringComparison.OrdinalIgnoreCase));

            if (preferred is not null)
            {
                return preferred;
            }
        }

        if (devices.Count == 1)
        {
            return devices[0];
        }

        return devices.FirstOrDefault(
            device =>
                device.Name.Contains(
                    "iPhone",
                    StringComparison.OrdinalIgnoreCase));
    }

    private Task ReleaseConnectionAsync()
    {
        if (_connection is not null)
        {
            try
            {
                _connection.Dispose();
            }
            catch
            {
            }

            _connection =
                null;
        }

        _connectedDeviceName =
            null;

        return Task.CompletedTask;
    }
}
