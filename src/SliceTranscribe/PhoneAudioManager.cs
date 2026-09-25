using Windows.Devices.Enumeration;
using Windows.Media.Audio;

namespace SliceTranscribe;

internal sealed class PhoneAudioManager :
    IAsyncDisposable
{
    private readonly string? _preferredName;
    private readonly TimeSpan _retryDelay =
        TimeSpan.FromSeconds(5);

    private readonly object _sync =
        new();

    private AudioPlaybackConnection? _connection;
    private CancellationTokenSource? _connectionCts;
    private string? _connectedDeviceName;

    private volatile bool _suspendedForCall;

    public PhoneAudioManager(
        string? preferredName)
    {
        _preferredName =
            string.IsNullOrWhiteSpace(preferredName)
                ? null
                : preferredName;
    }

    public bool IsConnected
    {
        get
        {
            lock (_sync)
            {
                return _connection is not null;
            }
        }
    }

    public string? ConnectedDeviceName
    {
        get
        {
            lock (_sync)
            {
                return _connectedDeviceName;
            }
        }
    }

    public bool IsSuspendedForCall =>
        _suspendedForCall;

    public void SetSuspendedForCall(
        bool suspended)
    {
        if (_suspendedForCall ==
            suspended)
        {
            return;
        }

        _suspendedForCall =
            suspended;

        if (suspended)
        {
            Console.WriteLine(
                "PHONE A2DP -> suspended for HFP call");

            CancellationTokenSource? connectionCts;

            lock (_sync)
            {
                connectionCts =
                    _connectionCts;
            }

            try
            {
                connectionCts?.Cancel();
            }
            catch
            {
            }
        }
        else
        {
            Console.WriteLine(
                "PHONE A2DP -> call ended; reconnect enabled");
        }
    }

    public async Task RunAsync(
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (_suspendedForCall)
            {
                try
                {
                    await Task.Delay(
                        250,
                        cancellationToken);
                }
                catch (OperationCanceledException)
                    when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                continue;
            }

            try
            {
                DeviceInformation? device =
                    await FindPreferredDeviceAsync();

                if (_suspendedForCall)
                {
                    continue;
                }

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

                using var connectionCts =
                    CancellationTokenSource.CreateLinkedTokenSource(
                        cancellationToken);

                lock (_sync)
                {
                    _connection =
                        connection;

                    _connectionCts =
                        connectionCts;

                    _connectedDeviceName =
                        device.Name;
                }

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
                        if (!_suspendedForCall)
                        {
                            Console.WriteLine(
                                "PHONE -> disconnected; reconnect will be attempted automatically");
                        }

                        closed.TrySetResult(
                            true);
                    }
                }

                connection.StateChanged +=
                    StateChanged;

                try
                {
                    if (_suspendedForCall)
                    {
                        continue;
                    }

                    Console.WriteLine(
                        $"PHONE -> enabling {device.Name}");

                    await connection.StartAsync();

                    if (_suspendedForCall)
                    {
                        continue;
                    }

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

                    try
                    {
                        await closed.Task.WaitAsync(
                            connectionCts.Token);
                    }
                    catch (OperationCanceledException)
                        when (
                            _suspendedForCall &&
                            !cancellationToken.IsCancellationRequested)
                    {
                        // CallProfileMonitor intentionally canceled this
                        // A2DP ownership so Phone Link can establish HFP.
                    }
                }
                finally
                {
                    connection.StateChanged -=
                        StateChanged;

                    await ReleaseConnectionAsync(
                        connection);
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

    private Task ReleaseConnectionAsync(
        AudioPlaybackConnection? expected = null)
    {
        AudioPlaybackConnection? connectionToDispose =
            null;

        CancellationTokenSource? ctsToDispose =
            null;

        lock (_sync)
        {
            if (expected is not null &&
                _connection is not null &&
                !ReferenceEquals(
                    expected,
                    _connection))
            {
                return Task.CompletedTask;
            }

            connectionToDispose =
                _connection;

            ctsToDispose =
                _connectionCts;

            _connection =
                null;

            _connectionCts =
                null;

            _connectedDeviceName =
                null;
        }

        try
        {
            connectionToDispose?.Dispose();
        }
        catch
        {
        }

        try
        {
            ctsToDispose?.Dispose();
        }
        catch
        {
        }

        return Task.CompletedTask;
    }
}
