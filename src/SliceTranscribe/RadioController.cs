using NAudio.CoreAudioApi;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;

namespace SliceTranscribe;

internal static class RadioController
{
    private const string Host = "127.0.0.1";
    private const int Port = 4212;

    private static readonly SemaphoreSlim Gate =
        new(
            1,
            1);

    private static readonly HashSet<string> PauseReasons =
        new(
            StringComparer.OrdinalIgnoreCase);

    private enum VlcPlaybackState
    {
        Unknown = 0,
        Playing,
        Paused,
        Stopped
    }

    public static async Task<bool> RequestPauseAsync(
        string reason,
        CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(
            cancellationToken);

        try
        {
            PauseReasons.Add(
                reason);

            bool muted =
                await SetRadioSessionMutedAsync(
                    muted: true,
                    cancellationToken);

            if (!muted)
            {
                PauseReasons.Remove(
                    reason);

                return false;
            }

            Console.WriteLine(
                $"RADIO -> muted ({string.Join(", ", PauseReasons)})");

            return true;
        }
        finally
        {
            Gate.Release();
        }
    }

    public static async Task<bool> ReleasePauseAsync(
        string reason,
        CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(
            cancellationToken);

        try
        {
            PauseReasons.Remove(
                reason);

            if (PauseReasons.Count != 0)
            {
                return true;
            }

            bool unmuted =
                await SetRadioSessionMutedAsync(
                    muted: false,
                    cancellationToken);

            if (!unmuted)
            {
                return false;
            }

            await RecoverTransportIfNeededAsync(
                cancellationToken);

            Console.WriteLine(
                "RADIO -> unmuted");

            return true;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task<bool> SetRadioSessionMutedAsync(
        bool muted,
        CancellationToken cancellationToken)
    {
        for (int attempt = 0;
             attempt < 6;
             attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            bool found =
                false;

            try
            {
                using var enumerator =
                    new MMDeviceEnumerator();

                using MMDevice output =
                    enumerator.GetDefaultAudioEndpoint(
                        DataFlow.Render,
                        Role.Multimedia);

                AudioSessionManager manager =
                    output.AudioSessionManager;

                manager.RefreshSessions();

                SessionCollection sessions =
                    manager.Sessions;

                for (int i = 0;
                     i < sessions.Count;
                     i++)
                {
                    try
                    {
                        using AudioSessionControl session =
                            sessions[i];

                        uint processId =
                            session.GetProcessID;

                        if (!IsVlcProcess(
                            processId))
                        {
                            continue;
                        }

                        session.SimpleAudioVolume.Mute =
                            muted;

                        found =
                            true;
                    }
                    catch
                    {
                        // Audio sessions can disappear while enumerating them.
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"RADIO -> audio-session control failed: {ex.Message}");
            }

            if (found)
            {
                return true;
            }

            if (attempt < 5)
            {
                await Task.Delay(
                    150,
                    cancellationToken);
            }
        }

        Console.Error.WriteLine(
            "RADIO -> VLC audio session was not found");

        return false;
    }

    private static bool IsVlcProcess(
        uint processId)
    {
        if (processId == 0)
        {
            return false;
        }

        try
        {
            using Process process =
                Process.GetProcessById(
                    checked((int)processId));

            return string.Equals(
                process.ProcessName,
                "vlc",
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static async Task RecoverTransportIfNeededAsync(
        CancellationToken cancellationToken)
    {
        VlcPlaybackState state =
            await QueryStateAsync(
                cancellationToken);

        switch (state)
        {
            case VlcPlaybackState.Playing:
                return;

            case VlcPlaybackState.Paused:
                await SendCommandAsync(
                    "pause",
                    cancellationToken);

                return;

            case VlcPlaybackState.Stopped:
            case VlcPlaybackState.Unknown:
                await SendCommandAsync(
                    "play",
                    cancellationToken);

                return;
        }
    }

    private static async Task<VlcPlaybackState> QueryStateAsync(
        CancellationToken cancellationToken)
    {
        string? response =
            await SendAndReadAsync(
                "status",
                cancellationToken);

        if (response is null)
        {
            return VlcPlaybackState.Unknown;
        }

        string normalized =
            response.ToLowerInvariant();

        if (normalized.Contains(
            "state playing"))
        {
            return VlcPlaybackState.Playing;
        }

        if (normalized.Contains(
            "state paused"))
        {
            return VlcPlaybackState.Paused;
        }

        if (normalized.Contains(
            "state stopped"))
        {
            return VlcPlaybackState.Stopped;
        }

        return VlcPlaybackState.Unknown;
    }

    private static async Task<bool> SendCommandAsync(
        string command,
        CancellationToken cancellationToken)
    {
        string? response =
            await SendAndReadAsync(
                command,
                cancellationToken);

        return response is not null;
    }

    private static async Task<string?> SendAndReadAsync(
        string command,
        CancellationToken cancellationToken)
    {
        try
        {
            using var client =
                new TcpClient();

            await client.ConnectAsync(
                Host,
                Port,
                cancellationToken);

            await using NetworkStream stream =
                client.GetStream();

            byte[] bytes =
                Encoding.ASCII.GetBytes(
                    command + "\n");

            await stream.WriteAsync(
                bytes,
                cancellationToken);

            await stream.FlushAsync(
                cancellationToken);

            using var timeout =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);

            timeout.CancelAfter(
                TimeSpan.FromMilliseconds(500));

            using var reader =
                new StreamReader(
                    stream,
                    Encoding.ASCII,
                    detectEncodingFromByteOrderMarks: false,
                    bufferSize: 1024,
                    leaveOpen: true);

            var builder =
                new StringBuilder();

            char[] buffer =
                new char[1024];

            try
            {
                while (true)
                {
                    int read =
                        await reader.ReadAsync(
                            buffer.AsMemory(
                                0,
                                buffer.Length),
                            timeout.Token);

                    if (read == 0)
                    {
                        break;
                    }

                    builder.Append(
                        buffer,
                        0,
                        read);

                    if (!stream.DataAvailable)
                    {
                        await Task.Delay(
                            30,
                            timeout.Token);

                        if (!stream.DataAvailable)
                        {
                            break;
                        }
                    }
                }
            }
            catch (OperationCanceledException)
                when (!cancellationToken.IsCancellationRequested)
            {
                // VLC RC keeps the socket open; timeout ends the response read.
            }

            return builder.ToString();
        }
        catch (
            Exception ex)
            when (
                ex is SocketException or
                IOException or
                OperationCanceledException)
        {
            if (ex is OperationCanceledException &&
                cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            Console.Error.WriteLine(
                $"Radio control unavailable: {ex.Message}");

            return null;
        }
    }
}
