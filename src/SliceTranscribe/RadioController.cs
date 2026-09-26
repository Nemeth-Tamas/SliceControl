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

    private static string? _currentPreset;
    private static string? _currentStation;
    private static string? _currentUrl;

    private enum VlcPlaybackState
    {
        Unknown = 0,
        Playing,
        Paused,
        Stopped
    }

    public static IReadOnlyDictionary<string, string> ListPresets()
    {
        return RadioPresetStore.List();
    }

    public static string? CurrentPreset =>
        _currentPreset;

    public static string? CurrentStation =>
        _currentStation;

    public static string? CurrentUrl =>
        _currentUrl;

    public static async Task<string> PlayAsync(
        string presetOrUrl,
        CancellationToken cancellationToken = default)
    {
        string url =
            RadioPresetStore.Resolve(
                presetOrUrl);

        string? preset =
            RadioPresetStore.List().ContainsKey(
                presetOrUrl)
                ? presetOrUrl
                : null;

        string? station =
            preset is null
                ? null
                : RadioPresetStore.GetDisplayName(
                    preset);

        await Gate.WaitAsync(
            cancellationToken);

        try
        {
            await SendCommandAsync(
                "clear",
                cancellationToken);

            bool added =
                await SendCommandAsync(
                    $"add {url}",
                    cancellationToken);

            if (!added)
            {
                throw new InvalidOperationException(
                    "VLC did not accept the radio URL.");
            }

            await Task.Delay(
                350,
                cancellationToken);

            if (PauseReasons.Count != 0)
            {
                await SetRadioSessionMutedAsync(
                    muted: true,
                    cancellationToken);
            }
            else
            {
                await SetRadioSessionMutedAsync(
                    muted: false,
                    cancellationToken);
            }

            _currentPreset =
                preset;

            _currentStation =
                station ??
                presetOrUrl;

            _currentUrl =
                url;

            Console.WriteLine(
                $"RADIO -> playing {_currentStation}");

            DiagnosticLog.Event(
                "radio",
                "playing",
                new
                {
                    preset =
                        _currentPreset,
                    station =
                        _currentStation,
                    url =
                        _currentUrl,
                    pause_reasons =
                        PauseReasons.ToArray()
                });

            return url;
        }
        finally
        {
            Gate.Release();
        }
    }

    public static async Task<bool> StopAsync(
        CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(
            cancellationToken);

        try
        {
            bool stopped =
                await SendCommandAsync(
                    "stop",
                    cancellationToken);

            if (stopped)
            {
                Console.WriteLine(
                    "RADIO -> stopped");

                DiagnosticLog.Event(
                    "radio",
                    "stopped");
            }

            return stopped;
        }
        finally
        {
            Gate.Release();
        }
    }

    public static async Task<string> GetNowPlayingAsync(
        CancellationToken cancellationToken = default)
    {
        string? info =
            await SendAndReadAsync(
                "info",
                cancellationToken);

        if (!string.IsNullOrWhiteSpace(
            info))
        {
            foreach (string key in new[]
            {
                "now_playing",
                "title",
                "artist"
            })
            {
                string? value =
                    TryReadInfoField(
                        info,
                        key);

                if (!string.IsNullOrWhiteSpace(
                    value))
                {
                    return value;
                }
            }
        }

        string? title =
            await SendAndReadAsync(
                "get_title",
                cancellationToken);

        title =
            CleanScalarResponse(
                title);

        if (!string.IsNullOrWhiteSpace(
            title))
        {
            return title;
        }

        VlcPlaybackState state =
            await QueryStateAsync(
                cancellationToken);

        return state.ToString();
    }

    public static async Task<string> GetPlaybackStateAsync(
        CancellationToken cancellationToken = default)
    {
        VlcPlaybackState state =
            await QueryStateAsync(
                cancellationToken);

        return state.ToString();
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

                DiagnosticLog.Warning(
                    "radio",
                    "pause_failed",
                    new
                    {
                        reason,
                        pause_reasons =
                            PauseReasons.ToArray()
                    });

                return false;
            }

            Console.WriteLine(
                $"RADIO -> muted ({string.Join(", ", PauseReasons)})");

            DiagnosticLog.Event(
                "radio",
                "pause_requested",
                new
                {
                    reason,
                    pause_reasons =
                        PauseReasons.ToArray()
                });

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
                DiagnosticLog.Event(
                    "radio",
                    "pause_reason_released_still_held",
                    new
                    {
                        released_reason =
                            reason,
                        pause_reasons =
                            PauseReasons.ToArray()
                    });

                return true;
            }

            VlcPlaybackState stateBefore =
                await RecoverTransportIfNeededAsync(
                    cancellationToken);

            // A stopped/restarted VLC may not have a Core Audio session until
            // playback has actually resumed. Give Windows a moment to publish it.
            if (stateBefore is
                VlcPlaybackState.Stopped or
                VlcPlaybackState.Unknown)
            {
                await Task.Delay(
                    350,
                    cancellationToken);
            }

            bool unmuted =
                await SetRadioSessionMutedAsync(
                    muted: false,
                    cancellationToken);

            if (!unmuted)
            {
                DiagnosticLog.Warning(
                    "radio",
                    "unmute_deferred_no_session",
                    new
                    {
                        released_reason =
                            reason,
                        transport_state_before =
                            stateBefore.ToString()
                    });

                // No VLC Core Audio session means there is currently nothing
                // audible to unmute. The radio watchdog/playback path will
                // create a fresh unmuted session on its next healthy start.
                return true;
            }

            Console.WriteLine(
                "RADIO -> unmuted");

            DiagnosticLog.Event(
                "radio",
                "unmuted",
                new
                {
                    released_reason =
                        reason
                });

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
        const int maxAttempts =
            12;

        for (int attempt = 0;
             attempt < maxAttempts;
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

                DiagnosticLog.Error(
                    "radio",
                    "audio_session_control_failed",
                    ex,
                    new
                    {
                        muted,
                        attempt
                    });
            }

            if (found)
            {
                return true;
            }

            if (attempt < maxAttempts - 1)
            {
                await Task.Delay(
                    250,
                    cancellationToken);
            }
        }

        Console.Error.WriteLine(
            "RADIO -> VLC audio session was not found");

        DiagnosticLog.Warning(
            "radio",
            "vlc_audio_session_not_found",
            new
            {
                muted
            });

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

    private static async Task<VlcPlaybackState> RecoverTransportIfNeededAsync(
        CancellationToken cancellationToken)
    {
        VlcPlaybackState state =
            await QueryStateAsync(
                cancellationToken);

        DiagnosticLog.Event(
            "radio",
            "transport_checked",
            new
            {
                state =
                    state.ToString()
            });

        switch (state)
        {
            case VlcPlaybackState.Playing:
                return state;

            case VlcPlaybackState.Paused:
                await SendCommandAsync(
                    "pause",
                    cancellationToken);

                DiagnosticLog.Event(
                    "radio",
                    "transport_resumed_from_pause");

                return state;

            case VlcPlaybackState.Stopped:
            case VlcPlaybackState.Unknown:
                await SendCommandAsync(
                    "play",
                    cancellationToken);

                DiagnosticLog.Event(
                    "radio",
                    "transport_play_requested",
                    new
                    {
                        previous_state =
                            state.ToString()
                    });

                return state;

            default:
                return state;
        }
    }

    private static async Task<VlcPlaybackState> QueryStateAsync(
        CancellationToken cancellationToken)
    {
        string? playingResponse =
            await SendAndReadAsync(
                "is_playing",
                cancellationToken);

        string? playing =
            CleanScalarResponse(
                playingResponse);

        if (string.Equals(
            playing,
            "1",
            StringComparison.Ordinal))
        {
            return VlcPlaybackState.Playing;
        }

        string? response =
            await SendAndReadAsync(
                "status",
                cancellationToken);

        if (response is null)
        {
            return string.Equals(
                playing,
                "0",
                StringComparison.Ordinal)
                    ? VlcPlaybackState.Stopped
                    : VlcPlaybackState.Unknown;
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

        return string.Equals(
            playing,
            "0",
            StringComparison.Ordinal)
                ? VlcPlaybackState.Stopped
                : VlcPlaybackState.Unknown;
    }

    private static string? TryReadInfoField(
        string info,
        string key)
    {
        foreach (string rawLine in info.Split(
            new[]
            {
                '\r',
                '\n'
            },
            StringSplitOptions.RemoveEmptyEntries))
        {
            string line =
                rawLine.Trim()
                    .TrimStart(
                        '|',
                        '+',
                        '-')
                    .Trim();

            int colon =
                line.IndexOf(
                    ':');

            if (colon <= 0)
            {
                continue;
            }

            string field =
                line[..colon]
                    .Trim();

            if (!field.Equals(
                key,
                StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return line[(colon + 1)..]
                .Trim();
        }

        return null;
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

            using var reader =
                new StreamReader(
                    stream,
                    Encoding.UTF8,
                    detectEncodingFromByteOrderMarks: false,
                    bufferSize: 1024,
                    leaveOpen: true);

            // VLC's CLI sends a welcome message and prompt immediately after
            // connecting. Drain that first so it cannot be mistaken for the
            // command response.
            await ReadUntilPromptAsync(
                reader,
                cancellationToken);

            byte[] bytes =
                Encoding.UTF8.GetBytes(
                    command + "\n");

            await stream.WriteAsync(
                bytes,
                cancellationToken);

            await stream.FlushAsync(
                cancellationToken);

            string response =
                await ReadUntilPromptAsync(
                    reader,
                    cancellationToken);

            return StripTrailingPrompt(
                response);
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

            DiagnosticLog.Error(
                "radio",
                "vlc_control_unavailable",
                ex,
                new
                {
                    command
                });

            return null;
        }
    }

    private static async Task<string> ReadUntilPromptAsync(
        StreamReader reader,
        CancellationToken cancellationToken)
    {
        using var timeout =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);

        timeout.CancelAfter(
            TimeSpan.FromMilliseconds(
                1500));

        var builder =
            new StringBuilder();

        char[] one =
            new char[1];

        try
        {
            while (true)
            {
                int read =
                    await reader.ReadAsync(
                        one.AsMemory(
                            0,
                            1),
                        timeout.Token);

                if (read == 0)
                {
                    break;
                }

                builder.Append(
                    one[0]);

                if (
                    builder.Length >= 2 &&
                    builder[^2] == '>' &&
                    builder[^1] == ' ')
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            // Some VLC commands may return without another prompt on older
            // builds. Return whatever arrived before the timeout.
        }

        return builder.ToString();
    }

    private static string StripTrailingPrompt(
        string response)
    {
        string cleaned =
            response.Replace(
                "\0",
                string.Empty)
            .TrimEnd();

        if (cleaned.EndsWith(
            ">",
            StringComparison.Ordinal))
        {
            cleaned =
                cleaned[..^1]
                .TrimEnd();
        }

        return cleaned;
    }

    private static string? CleanScalarResponse(
        string? response)
    {
        if (string.IsNullOrWhiteSpace(
            response))
        {
            return null;
        }

        string[] lines =
            response.Split(
                new[]
                {
                    '\r',
                    '\n'
                },
                StringSplitOptions.RemoveEmptyEntries);

        for (int i = lines.Length - 1;
             i >= 0;
             i--)
        {
            string value =
                lines[i].Trim();

            if (
                value.Length == 0 ||
                value.Equals(
                    ">",
                    StringComparison.Ordinal))
            {
                continue;
            }

            return value;
        }

        return null;
    }

}
