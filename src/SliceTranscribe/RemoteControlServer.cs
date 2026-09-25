using NAudio.CoreAudioApi;
using NAudio.MediaFoundation;
using NAudio.Wave;
using SliceControl;
using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SliceTranscribe;

internal sealed class RemoteControlServer :
    IAsyncDisposable
{
    private const int PcmSampleRate = 48000;

    private readonly SliceDevice _slice;
    private readonly RecordingCoordinator _recording;
    private readonly HttpListener _listener =
        new();

    private readonly SemaphoreSlim _speakerGate =
        new(
            1,
            1);

    private readonly object _indicatorGate =
        new();

    private readonly string _token;
    private readonly string _announcementDirectory;

    private CancellationTokenSource? _announcementCts;
    private int _monitorCount;
    private bool _talkActive;
    private bool _announcementActive;

    public RemoteControlServer(
        SliceDevice slice,
        RecordingCoordinator recording,
        int port = 8787)
    {
        _slice =
            slice;

        _recording =
            recording;

        _token =
            LoadOrCreateToken();

        _announcementDirectory =
            Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.MyDocuments),
                "SliceTranscribe",
                "Announcements");

        Directory.CreateDirectory(
            _announcementDirectory);

        _listener.Prefixes.Add(
            $"http://+:{port}/");

        Port =
            port;
    }

    public int Port { get; }

    public string Token =>
        _token;

    public string AnnouncementDirectory =>
        _announcementDirectory;

    public async Task RunAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            _listener.Start();

            Console.WriteLine(
                $"REMOTE -> listening on http://0.0.0.0:{Port}/");

            Console.WriteLine(
                $"REMOTE -> token {_token}");

            Console.WriteLine(
                $"REMOTE -> announcements {_announcementDirectory}");

            Task indicatorTask =
                MaintainIndicatorAsync(
                    cancellationToken);

            while (!cancellationToken.IsCancellationRequested)
            {
                HttpListenerContext context;

                try
                {
                    context =
                        await _listener
                            .GetContextAsync()
                            .WaitAsync(
                                cancellationToken);
                }
                catch (OperationCanceledException)
                    when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (HttpListenerException)
                    when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                _ =
                    Task.Run(
                        () =>
                            HandleContextAsync(
                                context,
                                cancellationToken),
                        CancellationToken.None);
            }
        }
        catch (HttpListenerException ex)
        {
            Console.Error.WriteLine(
                $"REMOTE -> web server failed: {ex.Message}");
        }
        finally
        {
            try
            {
                _listener.Stop();
            }
            catch
            {
            }
        }
    }

    private async Task HandleContextAsync(
        HttpListenerContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            string path =
                context.Request.Url?.AbsolutePath
                ?? "/";

            if (path == "/" &&
                context.Request.HttpMethod == "GET")
            {
                await WriteHtmlAsync(
                    context.Response,
                    Html);

                return;
            }

            if (path == "/ws/monitor")
            {
                if (!IsAuthorized(
                    context.Request))
                {
                    context.Response.StatusCode =
                        401;

                    context.Response.Close();
                    return;
                }

                await HandleMonitorWebSocketAsync(
                    context,
                    cancellationToken);

                return;
            }

            if (path == "/ws/talk")
            {
                if (!IsAuthorized(
                    context.Request))
                {
                    context.Response.StatusCode =
                        401;

                    context.Response.Close();
                    return;
                }

                await HandleTalkWebSocketAsync(
                    context,
                    cancellationToken);

                return;
            }

            if (!IsAuthorized(
                context.Request))
            {
                await WriteJsonAsync(
                    context.Response,
                    401,
                    new
                    {
                        error =
                            "Invalid or missing remote-control token."
                    });

                return;
            }

            if (path == "/api/status" &&
                context.Request.HttpMethod == "GET")
            {
                await WriteJsonAsync(
                    context.Response,
                    200,
                    BuildStatus());

                return;
            }

            if (path == "/api/record/start" &&
                context.Request.HttpMethod == "POST")
            {
                string? file =
                    await _recording.StartAsync(
                        cancellationToken);

                await WriteJsonAsync(
                    context.Response,
                    200,
                    new
                    {
                        ok = true,
                        file
                    });

                return;
            }

            if (path == "/api/record/pause" &&
                context.Request.HttpMethod == "POST")
            {
                bool? paused =
                    await _recording.TogglePauseAsync(
                        cancellationToken);

                await WriteJsonAsync(
                    context.Response,
                    paused is null
                        ? 409
                        : 200,
                    new
                    {
                        ok =
                            paused is not null,
                        paused
                    });

                return;
            }

            if (path == "/api/record/stop" &&
                context.Request.HttpMethod == "POST")
            {
                string? file =
                    await _recording.StopAsync(
                        cancellationToken);

                await WriteJsonAsync(
                    context.Response,
                    200,
                    new
                    {
                        ok = true,
                        file
                    });

                return;
            }

            if (path == "/api/announce/play" &&
                context.Request.HttpMethod == "POST")
            {
                string? requested =
                    context.Request.QueryString["name"];

                if (string.IsNullOrWhiteSpace(
                    requested))
                {
                    await WriteJsonAsync(
                        context.Response,
                        400,
                        new
                        {
                            error =
                                "Missing announcement name."
                        });

                    return;
                }

                bool started =
                    StartAnnouncement(
                        requested);

                await WriteJsonAsync(
                    context.Response,
                    started
                        ? 202
                        : 409,
                    new
                    {
                        ok =
                            started
                    });

                return;
            }

            if (path == "/api/announce/stop" &&
                context.Request.HttpMethod == "POST")
            {
                StopAnnouncement();

                await WriteJsonAsync(
                    context.Response,
                    200,
                    new
                    {
                        ok = true
                    });

                return;
            }

            await WriteJsonAsync(
                context.Response,
                404,
                new
                {
                    error =
                        "Not found."
                });
        }
        catch (Exception ex)
        {
            try
            {
                await WriteJsonAsync(
                    context.Response,
                    500,
                    new
                    {
                        error =
                            ex.Message
                    });
            }
            catch
            {
            }

            Console.Error.WriteLine(
                $"REMOTE -> request failed: {ex.Message}");
        }
    }

    private object BuildStatus()
    {
        return new
        {
            online = true,

            recording =
                _recording.IsRecording,

            paused =
                _recording.IsPaused,

            quiet =
                SafeGetQuietMode(),

            monitoring =
                Volatile.Read(
                    ref _monitorCount),

            talking =
                _talkActive,

            announcement =
                _announcementActive,

            microphones =
                GetMicrophones(),

            announcements =
                GetAnnouncements()
        };
    }

    private string[] GetMicrophones()
    {
        try
        {
            using var enumerator =
                new MMDeviceEnumerator();

            MMDeviceCollection devices =
                enumerator.EnumerateAudioEndPoints(
                    DataFlow.Capture,
                    DeviceState.Active);

            var result =
                new List<string>();

            for (int i = 0;
                 i < devices.Count;
                 i++)
            {
                using MMDevice device =
                    devices[i];

                result.Add(
                    device.FriendlyName);
            }

            return result
                .Distinct(
                    StringComparer.OrdinalIgnoreCase)
                .OrderBy(
                    value =>
                        value,
                    StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private string[] GetAnnouncements()
    {
        try
        {
            return Directory
                .EnumerateFiles(
                    _announcementDirectory)
                .Where(
                    path =>
                        path.EndsWith(
                            ".wav",
                            StringComparison.OrdinalIgnoreCase) ||
                        path.EndsWith(
                            ".mp3",
                            StringComparison.OrdinalIgnoreCase))
                .Select(
                    Path.GetFileName)
                .Where(
                    name =>
                        !string.IsNullOrWhiteSpace(
                            name))
                .Cast<string>()
                .OrderBy(
                    name =>
                        name,
                    StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private async Task HandleMonitorWebSocketAsync(
        HttpListenerContext context,
        CancellationToken cancellationToken)
    {
        if (!context.Request.IsWebSocketRequest)
        {
            context.Response.StatusCode =
                400;

            context.Response.Close();
            return;
        }

        string? microphoneName =
            context.Request.QueryString["mic"];

        using MMDevice microphone =
            MicrophoneSelector.Resolve(
                microphoneName);

        HttpListenerWebSocketContext wsContext =
            await context.AcceptWebSocketAsync(
                subProtocol: null);

        WebSocket socket =
            wsContext.WebSocket;

        using var capture =
            new WasapiCapture(
                microphone);

        byte[] formatMessage =
            Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(
                    new
                    {
                        sampleRate =
                            capture.WaveFormat.SampleRate
                    }));

        await socket.SendAsync(
            new ArraySegment<byte>(
                formatMessage),
            WebSocketMessageType.Text,
            endOfMessage: true,
            cancellationToken);

        using var sendGate =
            new SemaphoreSlim(
                1,
                1);

        CancellationTokenSource linked =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);

        capture.DataAvailable +=
            (_, args) =>
            {
                byte[] pcm =
                    ConvertFirstChannelToPcm16(
                        args.Buffer,
                        args.BytesRecorded,
                        capture.WaveFormat);

                if (pcm.Length == 0 ||
                    socket.State !=
                        WebSocketState.Open)
                {
                    return;
                }

                _ =
                    SendBinaryAsync(
                        socket,
                        sendGate,
                        pcm,
                        linked.Token);
            };

        try
        {
            Interlocked.Increment(
                ref _monitorCount);

            RefreshIndicator();

            Console.WriteLine(
                $"REMOTE MONITOR -> {microphone.FriendlyName}");

            capture.StartRecording();

            byte[] buffer =
                new byte[1024];

            while (
                socket.State ==
                    WebSocketState.Open &&
                !linked.IsCancellationRequested)
            {
                WebSocketReceiveResult result =
                    await socket.ReceiveAsync(
                        new ArraySegment<byte>(
                            buffer),
                        linked.Token);

                if (result.MessageType ==
                    WebSocketMessageType.Close)
                {
                    break;
                }
            }
        }
        finally
        {
            try
            {
                capture.StopRecording();
            }
            catch
            {
            }

            linked.Cancel();
            linked.Dispose();

            Interlocked.Decrement(
                ref _monitorCount);

            RefreshIndicator();

            await CloseSocketQuietlyAsync(
                socket);

            socket.Dispose();

            Console.WriteLine(
                "REMOTE MONITOR -> stopped");
        }
    }

    private async Task HandleTalkWebSocketAsync(
        HttpListenerContext context,
        CancellationToken cancellationToken)
    {
        if (!context.Request.IsWebSocketRequest)
        {
            context.Response.StatusCode =
                400;

            context.Response.Close();
            return;
        }

        bool entered =
            await _speakerGate.WaitAsync(
                0,
                cancellationToken);

        if (!entered)
        {
            context.Response.StatusCode =
                409;

            context.Response.Close();
            return;
        }

        HttpListenerWebSocketContext wsContext;

        try
        {
            wsContext =
                await context.AcceptWebSocketAsync(
                    subProtocol: null);
        }
        catch
        {
            _speakerGate.Release();
            throw;
        }

        WebSocket socket =
            wsContext.WebSocket;

        bool restoreMute =
            SafeGetQuietMode();

        var provider =
            new BufferedWaveProvider(
                new WaveFormat(
                    PcmSampleRate,
                    16,
                    1))
            {
                BufferDuration =
                    TimeSpan.FromSeconds(
                        2),

                DiscardOnBufferOverflow =
                    true
            };

        using var output =
            new WaveOutEvent();

        try
        {
            _talkActive =
                true;

            await BeginRemoteOutputAsync(
                "remote-talk",
                restoreMute,
                cancellationToken);

            output.Init(
                provider);

            output.Play();

            RefreshIndicator();

            Console.WriteLine(
                "REMOTE TALK -> active");

            byte[] receive =
                new byte[32768];

            while (
                socket.State ==
                    WebSocketState.Open &&
                !cancellationToken.IsCancellationRequested)
            {
                WebSocketReceiveResult result =
                    await socket.ReceiveAsync(
                        new ArraySegment<byte>(
                            receive),
                        cancellationToken);

                if (result.MessageType ==
                    WebSocketMessageType.Close)
                {
                    break;
                }

                if (result.MessageType !=
                    WebSocketMessageType.Binary)
                {
                    continue;
                }

                provider.AddSamples(
                    receive,
                    0,
                    result.Count);
            }
        }
        finally
        {
            try
            {
                output.Stop();
            }
            catch
            {
            }

            _talkActive =
                false;

            await EndRemoteOutputAsync(
                "remote-talk",
                restoreMute);

            RefreshIndicator();

            await CloseSocketQuietlyAsync(
                socket);

            socket.Dispose();

            _speakerGate.Release();

            Console.WriteLine(
                "REMOTE TALK -> stopped");
        }
    }

    private bool StartAnnouncement(
        string requestedName)
    {
        string safeName =
            Path.GetFileName(
                requestedName);

        string path =
            Path.Combine(
                _announcementDirectory,
                safeName);

        if (!File.Exists(
            path))
        {
            return false;
        }

        lock (_indicatorGate)
        {
            if (_announcementActive ||
                _talkActive)
            {
                return false;
            }

            _announcementActive =
                true;

            _announcementCts =
                new CancellationTokenSource();
        }

        _ =
            Task.Run(
                async () =>
                {
                    try
                    {
                        await PlayAnnouncementAsync(
                            path,
                            _announcementCts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine(
                            $"REMOTE ANNOUNCEMENT -> {ex.Message}");
                    }
                    finally
                    {
                        lock (_indicatorGate)
                        {
                            _announcementActive =
                                false;

                            _announcementCts?.Dispose();

                            _announcementCts =
                                null;
                        }

                        RefreshIndicator();
                    }
                });

        RefreshIndicator();

        return true;
    }

    private void StopAnnouncement()
    {
        lock (_indicatorGate)
        {
            try
            {
                _announcementCts?.Cancel();
            }
            catch
            {
            }
        }
    }

    private async Task PlayAnnouncementAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await _speakerGate.WaitAsync(
            cancellationToken);

        bool restoreMute =
            SafeGetQuietMode();

        try
        {
            await BeginRemoteOutputAsync(
                "announcement",
                restoreMute,
                cancellationToken);

            using var reader =
                new AudioFileReader(
                    path);

            using var output =
                new WaveOutEvent();

            var stopped =
                new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);

            output.PlaybackStopped +=
                (_, _) =>
                    stopped.TrySetResult(
                        true);

            output.Init(
                reader);

            output.Play();

            Console.WriteLine(
                $"REMOTE ANNOUNCEMENT -> {Path.GetFileName(path)}");

            using CancellationTokenRegistration registration =
                cancellationToken.Register(
                    () =>
                    {
                        try
                        {
                            output.Stop();
                        }
                        catch
                        {
                        }
                    });

            await stopped.Task.WaitAsync(
                cancellationToken);
        }
        finally
        {
            await EndRemoteOutputAsync(
                "announcement",
                restoreMute);

            _speakerGate.Release();
        }
    }

    private async Task BeginRemoteOutputAsync(
        string reason,
        bool wasMuted,
        CancellationToken cancellationToken)
    {
        await RadioController.RequestPauseAsync(
            reason,
            cancellationToken);

        await PhoneAudioSessionController.RequestMuteAsync(
            reason,
            cancellationToken);

        if (wasMuted)
        {
            SystemAudioController.SetMuted(
                false);
        }
    }

    private async Task EndRemoteOutputAsync(
        string reason,
        bool restoreMute)
    {
        if (restoreMute)
        {
            SystemAudioController.SetMuted(
                true);
        }

        await PhoneAudioSessionController.ReleaseMuteAsync(
            reason,
            CancellationToken.None);

        await RadioController.ReleasePauseAsync(
            reason,
            CancellationToken.None);
    }

    private async Task MaintainIndicatorAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (
                    Volatile.Read(
                        ref _monitorCount) > 0 ||
                    _talkActive ||
                    _announcementActive)
                {
                    RefreshIndicator();
                }

                await Task.Delay(
                    750,
                    cancellationToken);
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void RefreshIndicator()
    {
        try
        {
            if (_recording.IsRecording)
            {
                if (_recording.IsPaused)
                {
                    _slice.Lights.ShowActiveMutedCall();
                }
                else
                {
                    _slice.Lights.ShowActiveCall();
                }

                return;
            }

            if (
                Volatile.Read(
                    ref _monitorCount) > 0 ||
                _talkActive ||
                _announcementActive)
            {
                _slice.Lights.ShowActiveCall();
            }
            else
            {
                _slice.Lights.Reset();
            }
        }
        catch
        {
        }
    }

    private bool IsAuthorized(
        HttpListenerRequest request)
    {
        string? supplied =
            request.Headers[
                "X-Slice-Token"]
            ?? request.QueryString[
                "token"];

        if (string.IsNullOrWhiteSpace(
            supplied))
        {
            return false;
        }

        byte[] left =
            Encoding.UTF8.GetBytes(
                supplied);

        byte[] right =
            Encoding.UTF8.GetBytes(
                _token);

        return left.Length ==
                   right.Length &&
               CryptographicOperations.FixedTimeEquals(
                   left,
                   right);
    }

    private static async Task SendBinaryAsync(
        WebSocket socket,
        SemaphoreSlim gate,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        try
        {
            await gate.WaitAsync(
                cancellationToken);

            try
            {
                if (socket.State !=
                    WebSocketState.Open)
                {
                    return;
                }

                await socket.SendAsync(
                    new ArraySegment<byte>(
                        payload),
                    WebSocketMessageType.Binary,
                    endOfMessage: true,
                    cancellationToken);
            }
            finally
            {
                gate.Release();
            }
        }
        catch
        {
        }
    }

    private static byte[] ConvertFirstChannelToPcm16(
        byte[] buffer,
        int bytesRecorded,
        WaveFormat format)
    {
        int channels =
            Math.Max(
                1,
                format.Channels);

        int bytesPerSample =
            Math.Max(
                1,
                format.BitsPerSample /
                8);

        int frameSize =
            bytesPerSample *
            channels;

        if (frameSize <= 0)
        {
            return Array.Empty<byte>();
        }

        int frames =
            bytesRecorded /
            frameSize;

        byte[] result =
            new byte[
                frames *
                2];

        WaveFormatExtensible? extensible =
            format as
                WaveFormatExtensible;

        bool ieeeFloat =
            (
                format.Encoding ==
                    WaveFormatEncoding.IeeeFloat ||
                (
                    extensible is not null &&
                    extensible.SubFormat ==
                        AudioSubtypes.MFAudioFormat_Float
                )
            ) &&
            format.BitsPerSample ==
                32;

        bool isPcm =
            format.Encoding ==
                WaveFormatEncoding.Pcm ||
            (
                extensible is not null &&
                extensible.SubFormat ==
                    AudioSubtypes.MFAudioFormat_PCM
            );

        for (int frame = 0;
             frame < frames;
             frame++)
        {
            int source =
                frame *
                frameSize;

            float sample;

            if (ieeeFloat)
            {
                sample =
                    BitConverter.ToSingle(
                        buffer,
                        source);
            }
            else if (
                isPcm &&
                format.BitsPerSample ==
                    16)
            {
                sample =
                    BitConverter.ToInt16(
                        buffer,
                        source) /
                    32768f;
            }
            else if (
                isPcm &&
                format.BitsPerSample ==
                    24)
            {
                int value =
                    buffer[source] |
                    (buffer[source + 1] << 8) |
                    (buffer[source + 2] << 16);

                if ((value & 0x00800000) != 0)
                {
                    value |=
                        unchecked(
                            (int)0xff000000);
                }

                sample =
                    value /
                    8388608f;
            }
            else if (
                isPcm &&
                format.BitsPerSample ==
                    32)
            {
                sample =
                    BitConverter.ToInt32(
                        buffer,
                        source) /
                    2147483648f;
            }
            else
            {
                return Array.Empty<byte>();
            }

            sample =
                Math.Clamp(
                    sample,
                    -1f,
                    1f);

            short pcmSample =
                (short)Math.Round(
                    sample *
                    short.MaxValue);

            int target =
                frame *
                2;

            result[target] =
                (byte)(
                    pcmSample &
                    0xff);

            result[target + 1] =
                (byte)(
                    (pcmSample >>
                     8) &
                    0xff);
        }

        return result;
    }

    private static bool SafeGetQuietMode()
    {
        try
        {
            return SystemAudioController.IsMuted;
        }
        catch
        {
            return false;
        }
    }

    private static async Task CloseSocketQuietlyAsync(
        WebSocket socket)
    {
        try
        {
            if (
                socket.State ==
                    WebSocketState.Open ||
                socket.State ==
                    WebSocketState.CloseReceived)
            {
                await socket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "closed",
                    CancellationToken.None);
            }
        }
        catch
        {
        }
    }

    private static async Task WriteHtmlAsync(
        HttpListenerResponse response,
        string html)
    {
        byte[] bytes =
            Encoding.UTF8.GetBytes(
                html);

        response.StatusCode =
            200;

        response.ContentType =
            "text/html; charset=utf-8";

        response.ContentLength64 =
            bytes.Length;

        await response.OutputStream.WriteAsync(
            bytes);

        response.Close();
    }

    private static async Task WriteJsonAsync(
        HttpListenerResponse response,
        int statusCode,
        object value)
    {
        byte[] bytes =
            JsonSerializer.SerializeToUtf8Bytes(
                value);

        response.StatusCode =
            statusCode;

        response.ContentType =
            "application/json; charset=utf-8";

        response.ContentLength64 =
            bytes.Length;

        await response.OutputStream.WriteAsync(
            bytes);

        response.Close();
    }

    private static string LoadOrCreateToken()
    {
        string directory =
            Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                "SliceAppliance");

        Directory.CreateDirectory(
            directory);

        string path =
            Path.Combine(
                directory,
                "remote-token.txt");

        if (File.Exists(
            path))
        {
            string existing =
                File.ReadAllText(
                    path)
                    .Trim();

            if (!string.IsNullOrWhiteSpace(
                existing))
            {
                return existing;
            }
        }

        string token =
            Convert.ToHexString(
                RandomNumberGenerator.GetBytes(
                    24));

        File.WriteAllText(
            path,
            token);

        return token;
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            StopAnnouncement();

            _listener.Close();
        }
        catch
        {
        }

        _speakerGate.Dispose();

        return ValueTask.CompletedTask;
    }

    private const string Html =
        """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>Slice Control</title>
<style>
body{font-family:system-ui,sans-serif;background:#111;color:#eee;margin:0;padding:20px}
main{max-width:760px;margin:auto}
.card{background:#1d1d1d;border:1px solid #333;border-radius:14px;padding:16px;margin:12px 0}
button,input,select{font:inherit;padding:10px 12px;border-radius:8px;border:1px solid #555;background:#292929;color:#fff}
button{cursor:pointer;margin:4px}
button.on{background:#275d32}
button.danger{background:#652d2d}
.row{display:flex;gap:8px;flex-wrap:wrap;align-items:center}
.small{opacity:.75;font-size:.9rem}
#status{white-space:pre-wrap}
</style>
</head>
<body>
<main>
<h1>Slice Control</h1>
<div class="card">
<div class="row">
<input id="token" type="password" placeholder="Remote token" style="flex:1">
<button onclick="saveToken()">Save token</button>
</div>
<div class="small">Token is stored only in this browser.</div>
</div>

<div class="card">
<h2>Status</h2>
<div id="status">Connecting…</div>
</div>

<div class="card">
<h2>Recording</h2>
<div class="row">
<button onclick="post('/api/record/start')">Start</button>
<button onclick="post('/api/record/pause')">Pause / Resume</button>
<button class="danger" onclick="post('/api/record/stop')">Stop</button>
</div>
</div>

<div class="card">
<h2>Live microphone</h2>
<div class="row">
<select id="mic" style="flex:1"></select>
<button id="listen" onclick="toggleListen()">Listen</button>
</div>
<div class="small">The Slice shows its green active light while remote listening is connected.</div>
</div>

<div class="card">
<h2>Intercom</h2>
<button id="talk" onclick="toggleTalk()">Talk OFF</button>
<div id="talkHint" class="small">Talk ducks Retro Radio and phone audio while your browser microphone is live.</div>
</div>

<div class="card">
<h2>Announcements</h2>
<div id="announcements"></div>
<button class="danger" onclick="post('/api/announce/stop')">Stop announcement</button>
</div>
</main>

<script>
let monitorSocket=null, monitorContext=null, nextPlay=0, monitorSampleRate=48000;
let talkSocket=null, talkContext=null, talkStream=null, talkProcessor=null;
let lastMics='';

const tokenBox=document.getElementById('token');
tokenBox.value=localStorage.getItem('sliceToken')||'';

if(!window.isSecureContext){
  document.getElementById('talkHint').textContent=
    'Talk needs HTTPS because browsers block microphone access on remote HTTP pages. Listen/record/announcements still work.';
}

function saveToken(){
  localStorage.setItem('sliceToken',tokenBox.value.trim());
  refresh();
}
function token(){ return tokenBox.value.trim(); }
function wsUrl(path){
  const proto=location.protocol==='https:'?'wss:':'ws:';
  return proto+'//'+location.host+path;
}
async function api(path,options={}){
  options.headers=Object.assign({},options.headers||{}, {'X-Slice-Token':token()});
  const r=await fetch(path,options);
  const data=await r.json().catch(()=>({}));
  if(!r.ok) throw new Error(data.error||('HTTP '+r.status));
  return data;
}
async function post(path){
  try{ await api(path,{method:'POST'}); await refresh(); }
  catch(e){ alert(e.message); }
}
async function refresh(){
  try{
    const s=await api('/api/status');
    document.getElementById('status').textContent=
      'Recording: '+(s.recording?(s.paused?'PAUSED':'ON'):'OFF')+'\n'+
      'Quiet mode: '+(s.quiet?'ON':'OFF')+'\n'+
      'Remote listeners: '+s.monitoring+'\n'+
      'Talk: '+(s.talking?'ON':'OFF')+'\n'+
      'Announcement: '+(s.announcement?'PLAYING':'OFF');

    const m=JSON.stringify(s.microphones||[]);
    if(m!==lastMics){
      lastMics=m;
      const select=document.getElementById('mic');
      const old=select.value;
      select.innerHTML='';
      for(const name of s.microphones||[]){
        const o=document.createElement('option');
        o.value=name; o.textContent=name; select.appendChild(o);
      }
      if([...select.options].some(o=>o.value===old)) select.value=old;
    }

    const box=document.getElementById('announcements');
    box.innerHTML='';
    for(const name of s.announcements||[]){
      const b=document.createElement('button');
      b.textContent=name.replace(/\.(wav|mp3)$/i,'');
      b.onclick=()=>post('/api/announce/play?name='+encodeURIComponent(name));
      box.appendChild(b);
    }
  }catch(e){
    document.getElementById('status').textContent='Offline / unauthorized: '+e.message;
  }
}
setInterval(refresh,1500);
refresh();

async function toggleListen(){
  if(monitorSocket){ stopListen(); return; }
  try{
    const mic=document.getElementById('mic').value;
    monitorContext=new AudioContext({sampleRate:48000});
    nextPlay=monitorContext.currentTime+0.08;
    monitorSocket=new WebSocket(wsUrl('/ws/monitor?token='+encodeURIComponent(token())+'&mic='+encodeURIComponent(mic)));
    monitorSocket.binaryType='arraybuffer';
    monitorSocket.onmessage=e=>{
      if(typeof e.data==='string'){
        try{
          const meta=JSON.parse(e.data);
          if(meta.sampleRate) monitorSampleRate=meta.sampleRate;
        }catch{}
        return;
      }
      const input=new Int16Array(e.data);
      const buf=monitorContext.createBuffer(1,input.length,monitorSampleRate);
      const out=buf.getChannelData(0);
      for(let i=0;i<input.length;i++) out[i]=input[i]/32768;
      const src=monitorContext.createBufferSource();
      src.buffer=buf; src.connect(monitorContext.destination);
      const now=monitorContext.currentTime;
      if(nextPlay<now+0.03) nextPlay=now+0.03;
      src.start(nextPlay);
      nextPlay+=buf.duration;
    };
    monitorSocket.onclose=stopListen;
    document.getElementById('listen').textContent='Stop listening';
    document.getElementById('listen').classList.add('on');
  }catch(e){ stopListen(); alert(e.message); }
}
function stopListen(){
  const ws=monitorSocket; monitorSocket=null;
  if(ws&&ws.readyState<2) ws.close();
  if(monitorContext){ monitorContext.close(); monitorContext=null; }
  document.getElementById('listen').textContent='Listen';
  document.getElementById('listen').classList.remove('on');
}

function resampleTo48k(input,inRate){
  if(inRate===48000) return input;
  const outLen=Math.max(1,Math.round(input.length*48000/inRate));
  const out=new Float32Array(outLen);
  const ratio=inRate/48000;
  for(let i=0;i<outLen;i++){
    const pos=i*ratio, a=Math.floor(pos), b=Math.min(input.length-1,a+1), f=pos-a;
    out[i]=input[a]*(1-f)+input[b]*f;
  }
  return out;
}
function floatToPcm16(input){
  const out=new Int16Array(input.length);
  for(let i=0;i<input.length;i++){
    const s=Math.max(-1,Math.min(1,input[i]));
    out[i]=s<0?s*32768:s*32767;
  }
  return out;
}
async function toggleTalk(){
  if(talkSocket){ stopTalk(); return; }
  if(!window.isSecureContext){
    alert('Talk needs HTTPS (or a VPN HTTPS proxy) so the browser can use your microphone.');
    return;
  }
  stopListen();
  try{
    talkStream=await navigator.mediaDevices.getUserMedia({audio:{echoCancellation:true,noiseSuppression:true,autoGainControl:true}});
    talkContext=new AudioContext();
    const source=talkContext.createMediaStreamSource(talkStream);
    talkProcessor=talkContext.createScriptProcessor(2048,1,1);
    const silent=talkContext.createGain(); silent.gain.value=0;
    source.connect(talkProcessor); talkProcessor.connect(silent); silent.connect(talkContext.destination);

    talkSocket=new WebSocket(wsUrl('/ws/talk?token='+encodeURIComponent(token())));
    talkSocket.binaryType='arraybuffer';
    talkProcessor.onaudioprocess=e=>{
      if(!talkSocket||talkSocket.readyState!==WebSocket.OPEN) return;
      const mono=e.inputBuffer.getChannelData(0);
      const pcm=floatToPcm16(resampleTo48k(mono,talkContext.sampleRate));
      talkSocket.send(pcm.buffer);
    };
    talkSocket.onclose=stopTalk;
    document.getElementById('talk').textContent='Talk ON';
    document.getElementById('talk').classList.add('on');
  }catch(e){ stopTalk(); alert(e.message); }
}
function stopTalk(){
  const ws=talkSocket; talkSocket=null;
  if(ws&&ws.readyState<2) ws.close();
  if(talkProcessor){ talkProcessor.disconnect(); talkProcessor=null; }
  if(talkStream){ talkStream.getTracks().forEach(t=>t.stop()); talkStream=null; }
  if(talkContext){ talkContext.close(); talkContext=null; }
  document.getElementById('talk').textContent='Talk OFF';
  document.getElementById('talk').classList.remove('on');
}
</script>
</body>
</html>
""";
}
