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

            bool paused =
                await EnsureSilentAsync(
                    cancellationToken);

            if (!paused)
            {
                PauseReasons.Remove(
                    reason);

                return false;
            }

            Console.WriteLine(
                $"RADIO -> paused ({string.Join(", ", PauseReasons)})");

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

            bool playing =
                await EnsurePlayingAsync(
                    cancellationToken);

            if (playing)
            {
                Console.WriteLine(
                    "RADIO -> resumed");
            }

            return playing;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task<bool> EnsureSilentAsync(
        CancellationToken cancellationToken)
    {
        VlcPlaybackState state =
            await QueryStateAsync(
                cancellationToken);

        switch (state)
        {
            case VlcPlaybackState.Paused:
            case VlcPlaybackState.Stopped:
                return true;

            case VlcPlaybackState.Playing:
                if (!await SendCommandAsync(
                    "pause",
                    cancellationToken))
                {
                    return false;
                }

                await Task.Delay(
                    120,
                    cancellationToken);

                return await QueryStateAsync(
                    cancellationToken) !=
                    VlcPlaybackState.Playing;

            default:
                Console.Error.WriteLine(
                    "RADIO -> VLC state unknown; refusing blind pause toggle");

                return false;
        }
    }

    private static async Task<bool> EnsurePlayingAsync(
        CancellationToken cancellationToken)
    {
        VlcPlaybackState state =
            await QueryStateAsync(
                cancellationToken);

        string? command =
            state switch
            {
                VlcPlaybackState.Playing =>
                    null,

                VlcPlaybackState.Paused =>
                    "pause",

                VlcPlaybackState.Stopped =>
                    "play",

                _ =>
                    "play"
            };

        if (command is null)
        {
            return true;
        }

        if (!await SendCommandAsync(
            command,
            cancellationToken))
        {
            return false;
        }

        await Task.Delay(
            250,
            cancellationToken);

        VlcPlaybackState after =
            await QueryStateAsync(
                cancellationToken);

        if (after ==
            VlcPlaybackState.Playing)
        {
            return true;
        }

        if (after ==
            VlcPlaybackState.Stopped &&
            command != "play")
        {
            if (!await SendCommandAsync(
                "play",
                cancellationToken))
            {
                return false;
            }

            await Task.Delay(
                350,
                cancellationToken);

            after =
                await QueryStateAsync(
                    cancellationToken);
        }

        if (after !=
            VlcPlaybackState.Playing)
        {
            Console.Error.WriteLine(
                $"RADIO -> resume failed; VLC state is {after}");

            return false;
        }

        return true;
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
