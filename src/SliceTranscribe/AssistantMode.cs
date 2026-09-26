using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.MediaFoundation;
using NAudio.Wave;
using SliceControl;

namespace SliceTranscribe;

internal sealed record AssistantStatus(
    bool Enabled,
    string State,
    bool HermesConfigured,
    string HermesSessionKey,
    string? HermesSessionId,
    string WakeEngine,
    string CommandEngine,
    string? MicrophoneName,
    string? CaptureFormat,
    long? CaptureAgeMs,
    long CaptureCallbackCount,
    long CaptureBytes,
    double LastCapturePeak,
    int CaptureRestartCount,
    long? LastWakeLatencyMs,
    string? LastWakeTranscript,
    long? LastRemoteCommandLatencyMs,
    long? LastLocalCommandLatencyMs,
    string? LastLocalCommandTranscript,
    string? LastCommand,
    string? LastReply,
    string? LastError);

internal sealed class AssistantMode :
    IAsyncDisposable
{
    private static readonly TimeSpan ProbeInterval =
        TimeSpan.FromSeconds(
            2);

    private static readonly TimeSpan RollingDuration =
        TimeSpan.FromSeconds(
            4);

    private static readonly TimeSpan MinimumListenDuration =
        TimeSpan.FromSeconds(
            1.5);

    private static readonly TimeSpan SilenceDuration =
        TimeSpan.FromSeconds(
            2.0);

    private static readonly TimeSpan MaximumListenDuration =
        TimeSpan.FromSeconds(
            30);

    private static readonly TimeSpan CaptureStallTimeout =
        TimeSpan.FromSeconds(
            4);

    private const float SpeechPeakThreshold =
        0.018f;

    private readonly object _gate =
        new();

    private readonly SliceDevice _slice;
    private readonly RecordingCoordinator _recording;
    private readonly string? _microphoneName;
    private readonly WhisperOneShotClient _remoteWhisper;
    private readonly LocalEnglishWhisperClient _wakeWhisper =
        LocalEnglishWhisperClient.CreateTinyEn();
    private readonly LocalEnglishWhisperClient _fallbackWhisper =
        LocalEnglishWhisperClient.CreateBaseEn();
    private readonly HermesAssistantClient _hermes =
        new();

    private MemoryStream _rolling =
        new();

    private MemoryStream _command =
        new();

    private WasapiCapture? _capture;
    private MMDevice? _microphone;
    private WaveFormat? _captureFormat;

    private bool _enabled =
        true;

    private bool _listening;
    private bool _processing;
    private bool _speaking;
    private bool _probeBusy;
    private bool _listenDuckHeld;
    private bool _commandSpeechDetected;

    private DateTimeOffset _nextProbe =
        DateTimeOffset.MinValue;

    private DateTimeOffset _listeningStarted;
    private DateTimeOffset _lastSpeech;
    private DateTimeOffset _lastIdleSpeech =
        DateTimeOffset.MinValue;

    private DateTimeOffset _lastCaptureData =
        DateTimeOffset.MinValue;

    private long _captureCallbackCount;
    private long _captureBytes;
    private double _lastCapturePeak;
    private int _captureRestartCount;

    private long? _lastWakeLatencyMs;
    private long? _lastRemoteCommandLatencyMs;
    private long? _lastLocalCommandLatencyMs;
    private string? _lastLocalCommandTranscript;
    private string? _lastWakeTranscript;
    private string? _lastCommand;
    private string? _lastReply;
    private string? _lastError;

    private CancellationToken _runCancellationToken;

    public AssistantMode(
        SliceDevice slice,
        RecordingCoordinator recording,
        string? microphoneName,
        string whisperUrl)
    {
        _slice =
            slice;

        _recording =
            recording;

        _microphoneName =
            microphoneName;

        _remoteWhisper =
            new WhisperOneShotClient(
                whisperUrl);

        _enabled =
            _hermes.IsConfigured;
    }

    public bool Enabled
    {
        get
        {
            lock (_gate)
            {
                return _enabled;
            }
        }
    }

    public AssistantStatus GetStatus()
    {
        lock (_gate)
        {
            return new AssistantStatus(
                Enabled:
                    _enabled,
                State:
                    GetStateLocked(),
                HermesConfigured:
                    _hermes.IsConfigured,
                HermesSessionKey:
                    _hermes.SessionKey,
                HermesSessionId:
                    _hermes.SessionId,
                WakeEngine:
                    "local tiny.en CPU",
                CommandEngine:
                    "remote large-v3 -> local base.en fallback",
                MicrophoneName:
                    _microphone?.FriendlyName,
                CaptureFormat:
                    _captureFormat?.ToString(),
                CaptureAgeMs:
                    _lastCaptureData == DateTimeOffset.MinValue
                        ? null
                        : Math.Max(
                            0,
                            (long)(
                                DateTimeOffset.UtcNow -
                                _lastCaptureData).TotalMilliseconds),
                CaptureCallbackCount:
                    Interlocked.Read(
                        ref _captureCallbackCount),
                CaptureBytes:
                    Interlocked.Read(
                        ref _captureBytes),
                LastCapturePeak:
                    _lastCapturePeak,
                CaptureRestartCount:
                    _captureRestartCount,
                LastWakeLatencyMs:
                    _lastWakeLatencyMs,
                LastWakeTranscript:
                    _lastWakeTranscript,
                LastRemoteCommandLatencyMs:
                    _lastRemoteCommandLatencyMs,
                LastLocalCommandLatencyMs:
                    _lastLocalCommandLatencyMs,
                LastLocalCommandTranscript:
                    _lastLocalCommandTranscript,
                LastCommand:
                    _lastCommand,
                LastReply:
                    _lastReply,
                LastError:
                    _lastError);
        }
    }

    public void SetEnabled(
        bool enabled)
    {
        lock (_gate)
        {
            _enabled =
                enabled;

            if (!enabled)
            {
                _rolling.SetLength(
                    0);

                _command.SetLength(
                    0);

                _listening =
                    false;

                _processing =
                    false;

                _commandSpeechDetected =
                    false;
            }
        }

        if (!enabled)
        {
            _ =
                EndListeningDuckAsync();

            RefreshLights();
        }

        Console.WriteLine(
            enabled
                ? "ASSISTANT -> enabled, wake word ECHO"
                : "ASSISTANT -> disabled");

        DiagnosticLog.Event(
            "assistant",
            enabled
                ? "enabled"
                : "disabled",
            new
            {
                state =
                    GetStatus().State
            });
    }

    public async Task RunAsync(
        CancellationToken cancellationToken)
    {
        _runCancellationToken =
            cancellationToken;

        Console.WriteLine(
            "ASSISTANT -> wake detection uses local Whisper tiny.en on the Slice CPU only");

        Console.WriteLine(
            "ASSISTANT -> commands use remote large-v3 with local base.en fallback");

        Console.WriteLine(
            _hermes.IsConfigured
                ? $"ASSISTANT -> Hermes ready; session key {_hermes.SessionKey}"
                : $"ASSISTANT -> Hermes not configured; run Configure-SliceAssistant.ps1 ({_hermes.ConfigPath})");

        DiagnosticLog.Event(
            "assistant",
            "startup",
            new
            {
                enabled =
                    _enabled,
                hermes_configured =
                    _hermes.IsConfigured,
                session_key =
                    _hermes.SessionKey,
                session_id =
                    _hermes.SessionId,
                wake_engine =
                    "tiny.en-cpu",
                command_engine =
                    "remote-large-v3-with-base.en-fallback"
            });

        if (_enabled)
        {
            _ =
                Task.Run(
                    () =>
                        NeuralTtsSpeaker.WarmUpAsync(
                            cancellationToken),
                    CancellationToken.None);

            _ =
                Task.Run(
                    () =>
                        _wakeWhisper.WarmUpAsync(
                            cancellationToken),
                    CancellationToken.None);

            _ =
                Task.Run(
                    () =>
                        _fallbackWhisper.WarmUpAsync(
                            cancellationToken),
                    CancellationToken.None);
        }

        int restartCount =
            0;

        while (!cancellationToken.IsCancellationRequested)
        {
            WasapiCapture? capture =
                null;

            MMDevice? microphone =
                null;

            EventHandler<StoppedEventArgs>? stoppedHandler =
                null;

            var stopped =
                new TaskCompletionSource<Exception?>(
                    TaskCreationOptions.RunContinuationsAsynchronously);

            try
            {
                microphone =
                    MicrophoneSelector.Resolve(
                        _microphoneName);

                capture =
                    new WasapiCapture(
                        microphone);

                _microphone =
                    microphone;

                _capture =
                    capture;

                _captureFormat =
                    capture.WaveFormat;

                capture.DataAvailable +=
                    OnDataAvailable;

                stoppedHandler =
                    (_, e) =>
                        stopped.TrySetResult(
                            e.Exception);

                capture.RecordingStopped +=
                    stoppedHandler;

                capture.StartRecording();

                _lastCaptureData =
                    DateTimeOffset.UtcNow;

                Console.WriteLine(
                    restartCount == 0
                        ? $"ASSISTANT -> ECHO wake mode on {microphone.FriendlyName}"
                        : $"ASSISTANT MIC -> recovered on {microphone.FriendlyName} (restart {restartCount})");

                _captureRestartCount =
                    restartCount;

                DiagnosticLog.Event(
                    "mic",
                    restartCount == 0
                        ? "capture_opened"
                        : "capture_recovered",
                    new
                    {
                        device =
                            microphone.FriendlyName,
                        device_id =
                            microphone.ID,
                        format =
                            capture.WaveFormat.ToString(),
                        restart_count =
                            restartCount,
                        callback_count =
                            Interlocked.Read(
                                ref _captureCallbackCount),
                        captured_bytes =
                            Interlocked.Read(
                                ref _captureBytes)
                    });

                Exception? stopError =
                    null;

                bool stalled =
                    false;

                while (!cancellationToken.IsCancellationRequested)
                {
                    Task delay =
                        Task.Delay(
                            TimeSpan.FromSeconds(
                                1),
                            cancellationToken);

                    Task completed =
                        await Task.WhenAny(
                            stopped.Task,
                            delay);

                    if (completed ==
                        stopped.Task)
                    {
                        stopError =
                            await stopped.Task;

                        break;
                    }

                    DateTimeOffset lastData =
                        _lastCaptureData;

                    if (
                        lastData !=
                            DateTimeOffset.MinValue &&
                        DateTimeOffset.UtcNow -
                            lastData >=
                            CaptureStallTimeout)
                    {
                        stalled =
                            true;

                        break;
                    }
                }

                if (!cancellationToken.IsCancellationRequested)
                {
                    if (stalled)
                    {
                        long callbackAgeMs =
                            _lastCaptureData == DateTimeOffset.MinValue
                                ? -1
                                : (long)(
                                    DateTimeOffset.UtcNow -
                                    _lastCaptureData).TotalMilliseconds;

                        Console.Error.WriteLine(
                            $"ASSISTANT MIC -> capture stalled for {CaptureStallTimeout.TotalSeconds:0} s; reopening in 1 s");

                        DiagnosticLog.Warning(
                            "mic",
                            "capture_stalled",
                            new
                            {
                                callback_age_ms =
                                    callbackAgeMs,
                                callback_count =
                                    Interlocked.Read(
                                        ref _captureCallbackCount),
                                captured_bytes =
                                    Interlocked.Read(
                                        ref _captureBytes),
                                last_peak =
                                    _lastCapturePeak,
                                assistant_state =
                                    GetStatus().State,
                                recording_active =
                                    _recording.IsRecording,
                                restart_count =
                                    restartCount
                            });
                    }
                    else
                    {
                        Console.Error.WriteLine(
                            stopError is null
                                ? "ASSISTANT MIC -> capture stopped unexpectedly; reopening in 1 s"
                                : $"ASSISTANT MIC -> capture failed: {stopError.GetType().Name}: {stopError.Message}; reopening in 1 s");

                        if (stopError is null)
                        {
                            DiagnosticLog.Warning(
                                "mic",
                                "capture_stopped",
                                new
                                {
                                    callback_count =
                                        Interlocked.Read(
                                            ref _captureCallbackCount),
                                    captured_bytes =
                                        Interlocked.Read(
                                            ref _captureBytes),
                                    last_peak =
                                        _lastCapturePeak,
                                    assistant_state =
                                        GetStatus().State
                                });
                        }
                        else
                        {
                            DiagnosticLog.Error(
                                "mic",
                                "capture_failed",
                                stopError,
                                new
                                {
                                    callback_count =
                                        Interlocked.Read(
                                            ref _captureCallbackCount),
                                    captured_bytes =
                                        Interlocked.Read(
                                            ref _captureBytes),
                                    last_peak =
                                        _lastCapturePeak,
                                    assistant_state =
                                        GetStatus().State
                                });
                        }
                    }

                    ResetCaptureState();

                    await EndListeningDuckAsync();
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
                    $"ASSISTANT MIC -> open/capture error: {ex.GetType().Name}: {ex.Message}; reopening in 1 s");

                DiagnosticLog.Error(
                    "mic",
                    "open_or_capture_error",
                    ex,
                    new
                    {
                        requested_microphone =
                            _microphoneName,
                        restart_count =
                            restartCount,
                        assistant_state =
                            GetStatus().State
                    });

                ResetCaptureState();

                await EndListeningDuckAsync();
            }
            finally
            {
                if (capture is not null)
                {
                    try
                    {
                        capture.StopRecording();
                    }
                    catch
                    {
                    }

                    capture.DataAvailable -=
                        OnDataAvailable;

                    if (stoppedHandler is not null)
                    {
                        capture.RecordingStopped -=
                            stoppedHandler;
                    }

                    capture.Dispose();
                }

                microphone?.Dispose();

                if (ReferenceEquals(
                    _capture,
                    capture))
                {
                    _capture =
                        null;

                    _captureFormat =
                        null;
                }

                if (ReferenceEquals(
                    _microphone,
                    microphone))
                {
                    _microphone =
                        null;
                }
            }

            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            restartCount++;

            try
            {
                await Task.Delay(
                    TimeSpan.FromSeconds(
                        1),
                    cancellationToken);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }

        await EndListeningDuckAsync();
    }

    private void ResetCaptureState()
    {
        lock (_gate)
        {
            _rolling.SetLength(
                0);

            _command.SetLength(
                0);

            _listening =
                false;

            _processing =
                false;

            _probeBusy =
                false;

            _commandSpeechDetected =
                false;

            _lastIdleSpeech =
                DateTimeOffset.MinValue;

            _nextProbe =
                DateTimeOffset.MinValue;

            _lastCaptureData =
                DateTimeOffset.MinValue;
        }
    }

    private void OnDataAvailable(
        object? sender,
        WaveInEventArgs e)
    {
        _lastCaptureData =
            DateTimeOffset.UtcNow;

        WaveFormat? format =
            _captureFormat;

        if (format is null)
        {
            return;
        }

        Interlocked.Increment(
            ref _captureCallbackCount);

        Interlocked.Add(
            ref _captureBytes,
            e.BytesRecorded);

        float callbackPeak =
            MeasurePeak(
                e.Buffer,
                e.BytesRecorded,
                format);

        _lastCapturePeak =
            callbackPeak;

        byte[]? wakeSnapshot =
            null;

        byte[]? commandSnapshot =
            null;

        string? commandFinishReason =
            null;

        long commandListenAgeMs =
            0;

        long commandSilenceAgeMs =
            0;

        DateTimeOffset now =
            DateTimeOffset.UtcNow;

        lock (_gate)
        {
            if (
                !_enabled ||
                _speaking ||
                _processing ||
                _recording.IsRecording)
            {
                _rolling.SetLength(
                    0);

                return;
            }

            if (_listening)
            {
                _command.Write(
                    e.Buffer,
                    0,
                    e.BytesRecorded);

                if (callbackPeak >=
                    SpeechPeakThreshold)
                {
                    _commandSpeechDetected =
                        true;

                    _lastSpeech =
                        now;
                }

                TimeSpan listenAge =
                    now -
                    _listeningStarted;

                bool silenceFinished =
                    _commandSpeechDetected &&
                    listenAge >=
                        MinimumListenDuration &&
                    now -
                        _lastSpeech >=
                        SilenceDuration;

                bool timedOut =
                    listenAge >=
                        MaximumListenDuration;

                if (
                    silenceFinished ||
                    timedOut)
                {
                    commandSnapshot =
                        _command.ToArray();

                    commandFinishReason =
                        silenceFinished
                            ? "silence"
                            : "timeout";

                    commandListenAgeMs =
                        (long)
                            listenAge.TotalMilliseconds;

                    commandSilenceAgeMs =
                        (long)(
                            now -
                            _lastSpeech).TotalMilliseconds;

                    _command.SetLength(
                        0);

                    _listening =
                        false;

                    _processing =
                        true;
                }
            }
            else
            {
                _rolling.Write(
                    e.Buffer,
                    0,
                    e.BytesRecorded);

                TrimRollingLocked(
                    format);

                if (callbackPeak >=
                    SpeechPeakThreshold)
                {
                    _lastIdleSpeech =
                        now;
                }

                bool recentSpeech =
                    now -
                        _lastIdleSpeech <=
                    TimeSpan.FromSeconds(
                        1.5);

                if (
                    recentSpeech &&
                    !_probeBusy &&
                    now >=
                        _nextProbe &&
                    _rolling.Length >=
                        format.AverageBytesPerSecond *
                        2)
                {
                    _probeBusy =
                        true;

                    _nextProbe =
                        now +
                        ProbeInterval;

                    wakeSnapshot =
                        _rolling.ToArray();
                }
            }
        }

        if (wakeSnapshot is not null)
        {
            _ =
                ProbeWakeAsync(
                    wakeSnapshot,
                    format,
                    _runCancellationToken);
        }

        if (commandSnapshot is not null)
        {
            DiagnosticLog.Event(
                "assistant",
                "command_capture_complete",
                new
                {
                    reason =
                        commandFinishReason,
                    audio_bytes =
                        commandSnapshot.Length,
                    listen_age_ms =
                        commandListenAgeMs,
                    silence_age_ms =
                        commandSilenceAgeMs,
                    speech_detected =
                        _commandSpeechDetected,
                    last_peak =
                        callbackPeak,
                    callback_count =
                        Interlocked.Read(
                            ref _captureCallbackCount)
                });

            _ =
                ProcessCommandAsync(
                    commandSnapshot,
                    format,
                    _runCancellationToken);
        }
    }

    private async Task ProbeWakeAsync(
        byte[] audio,
        WaveFormat format,
        CancellationToken cancellationToken)
    {
        try
        {
            LocalWhisperResult local =
                await _wakeWhisper.TranscribeAsync(
                    audio,
                    format,
                    prompt:
                        "Echo. Calendar. Schedule. Client. Appointment. Reminder.",
                    cancellationToken);

            string text =
                local.Text;

            long latencyMs =
                local.ElapsedMilliseconds;

            lock (_gate)
            {
                _lastWakeLatencyMs =
                    latencyMs;
            }

            if (!ContainsWakeWord(
                text))
            {
                return;
            }

            bool startListening =
                false;

            lock (_gate)
            {
                _lastWakeTranscript =
                    text;

                if (
                    _enabled &&
                    !_listening &&
                    !_processing &&
                    !_speaking &&
                    !_recording.IsRecording)
                {
                    byte[] preRoll =
                        _rolling.ToArray();

                    _command.SetLength(
                        0);

                    _command.Write(
                        preRoll,
                        0,
                        preRoll.Length);

                    _listening =
                        true;

                    _listeningStarted =
                        DateTimeOffset.UtcNow;

                    _commandSpeechDetected =
                        !string.IsNullOrWhiteSpace(
                            StripWakeWord(
                                text));

                    _lastSpeech =
                        _listeningStarted;

                    startListening =
                        true;
                }
            }

            if (startListening)
            {
                Console.WriteLine();
                Console.WriteLine(
                    $"ASSISTANT WAKE -> {text} ({latencyMs} ms)");

                DiagnosticLog.Event(
                    "assistant",
                    "wake_detected",
                    new
                    {
                        transcript =
                            text,
                        wake_latency_ms =
                            latencyMs,
                        pre_roll_bytes =
                            _command.Length,
                        command_speech_already_detected =
                            _commandSpeechDetected,
                        capture_age_ms =
                            _lastCaptureData == DateTimeOffset.MinValue
                                ? -1
                                : (long)(
                                    DateTimeOffset.UtcNow -
                                    _lastCaptureData).TotalMilliseconds
                    });

                await BeginListeningDuckAsync(
                    cancellationToken);

                TryShowActiveCall();
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                _lastError =
                    ex.Message;
            }

            Console.Error.WriteLine(
                $"ASSISTANT WAKE ERROR -> {ex.Message}");

            DiagnosticLog.Error(
                "assistant",
                "wake_error",
                ex,
                new
                {
                    state =
                        GetStatus().State
                });
        }
        finally
        {
            lock (_gate)
            {
                _probeBusy =
                    false;
            }
        }
    }

    private async Task ProcessCommandAsync(
        byte[] audio,
        WaveFormat format,
        CancellationToken cancellationToken)
    {
        try
        {
            string transcript;

            DiagnosticLog.Event(
                "stt",
                "remote_start",
                new
                {
                    audio_bytes =
                        audio.Length,
                    format =
                        format.ToString()
                });

            var remoteWatch =
                Stopwatch.StartNew();

            try
            {
                transcript =
                    await _remoteWhisper.TranscribeAsync(
                        audio,
                        format,
                        language:
                            "en",
                        prompt:
                            "Echo. Calendar. Schedule. Client. Appointment. Reminder. Email. Radio. Cellnet.",
                        cancellationToken);

                remoteWatch.Stop();

                lock (_gate)
                {
                    _lastRemoteCommandLatencyMs =
                        remoteWatch.ElapsedMilliseconds;

                    _lastLocalCommandLatencyMs =
                        null;

                    _lastLocalCommandTranscript =
                        null;
                }

                Console.WriteLine(
                    $"REMOTE STT -> {remoteWatch.ElapsedMilliseconds} ms -> {transcript}");

                DiagnosticLog.Event(
                    "stt",
                    "remote_complete",
                    new
                    {
                        latency_ms =
                            remoteWatch.ElapsedMilliseconds,
                        transcript =
                            transcript,
                        transcript_chars =
                            transcript.Length
                    });
            }
            catch (Exception ex)
                when (
                    ex is not OperationCanceledException ||
                    !cancellationToken.IsCancellationRequested)
            {
                remoteWatch.Stop();

                Console.Error.WriteLine(
                    $"REMOTE STT -> failed after {remoteWatch.ElapsedMilliseconds} ms: {ex.Message}");

                DiagnosticLog.Error(
                    "stt",
                    "remote_failed",
                    ex,
                    new
                    {
                        latency_ms =
                            remoteWatch.ElapsedMilliseconds,
                        fallback =
                            "base.en"
                    });

                DiagnosticLog.Event(
                    "stt",
                    "fallback_start",
                    new
                    {
                        model =
                            "base.en",
                        audio_bytes =
                            audio.Length
                    });

                LocalWhisperResult fallback =
                    await _fallbackWhisper.TranscribeAsync(
                        audio,
                        format,
                        prompt:
                            "Echo. Calendar. Schedule. Client. Appointment. Reminder. Email. Radio. Cellnet.",
                        cancellationToken);

                transcript =
                    fallback.Text;

                lock (_gate)
                {
                    _lastLocalCommandLatencyMs =
                        fallback.ElapsedMilliseconds;

                    _lastLocalCommandTranscript =
                        fallback.Text;
                }

                Console.WriteLine(
                    $"LOCAL base.en FALLBACK -> {fallback.ElapsedMilliseconds} ms -> {fallback.Text}");

                DiagnosticLog.Event(
                    "stt",
                    "fallback_complete",
                    new
                    {
                        model =
                            "base.en",
                        latency_ms =
                            fallback.ElapsedMilliseconds,
                        transcript =
                            fallback.Text,
                        transcript_chars =
                            fallback.Text.Length
                    });
            }

            string command =
                StripWakeWord(
                    transcript);

            if (string.IsNullOrWhiteSpace(
                command))
            {
                Console.WriteLine(
                    "ASSISTANT -> wake heard, but no command was understood");

                await EndListeningDuckAsync();

                lock (_gate)
                {
                    _processing =
                        false;
                }

                await SpeakTransientAsync(
                    "I didn't catch that.",
                    cancellationToken);

                return;
            }

            lock (_gate)
            {
                _lastCommand =
                    command;

                _lastReply =
                    null;

                _lastError =
                    null;
            }

            Console.WriteLine();
            Console.WriteLine(
                $"ASSISTANT COMMAND -> {command}");

            DiagnosticLog.Event(
                "assistant",
                "command_ready",
                new
                {
                    command =
                        command,
                    chars =
                        command.Length
                });

            if (!_hermes.IsConfigured)
            {
                throw new InvalidOperationException(
                    $"Hermes is not configured. Run Configure-SliceAssistant.ps1. Config: {_hermes.ConfigPath}");
            }

            string reply;

            var hermesWatch =
                Stopwatch.StartNew();

            DiagnosticLog.Event(
                "hermes",
                "request_start",
                new
                {
                    session_id =
                        _hermes.SessionId,
                    session_key =
                        _hermes.SessionKey,
                    command_chars =
                        command.Length
                });

            using (
                ThinkingSoundPlayer thinking =
                    ThinkingSoundPlayer.Start())
            {
                reply =
                    await _hermes.SendAsync(
                        command,
                        cancellationToken);
            }

            hermesWatch.Stop();

            DiagnosticLog.Event(
                "hermes",
                "request_complete",
                new
                {
                    session_id =
                        _hermes.SessionId,
                    latency_ms =
                        hermesWatch.ElapsedMilliseconds,
                    reply_chars =
                        reply.Length,
                    reply =
                        reply
                });

            lock (_gate)
            {
                _lastReply =
                    reply;

                _processing =
                    false;

                _rolling.SetLength(
                    0);
            }

            Console.WriteLine(
                $"ASSISTANT HERMES -> {reply}");

            try
            {
                await SpeakTransientAsync(
                    reply,
                    cancellationToken);
            }
            finally
            {
                await EndListeningDuckAsync();
            }

            RefreshLights();
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                _processing =
                    false;

                _lastError =
                    ex.Message;
            }

            await EndListeningDuckAsync();

            RefreshLights();

            Console.Error.WriteLine(
                $"ASSISTANT COMMAND ERROR -> {ex.Message}");

            DiagnosticLog.Error(
                "assistant",
                "command_error",
                ex,
                new
                {
                    state =
                        GetStatus().State,
                    listen_duck_held =
                        _listenDuckHeld
                });
        }
    }

    private async Task SpeakTransientAsync(
        string text,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _speaking =
                true;
        }

        bool restoreMute =
            SafeGetMuted();

        int originalVolume =
            SafeGetVolumePercent();

        int boostedVolume =
            Math.Min(
                100,
                originalVolume +
                10);

        bool volumeBoosted =
            boostedVolume !=
            originalVolume;

        var ttsWatch =
            Stopwatch.StartNew();

        DiagnosticLog.Event(
            "tts",
            "speak_start",
            new
            {
                chars =
                    text.Length,
                original_volume =
                    originalVolume,
                target_volume =
                    boostedVolume,
                restore_mute =
                    restoreMute
            });

        try
        {
            await RadioController.RequestPauseAsync(
                "echo-tts",
                cancellationToken);

            await PhoneAudioSessionController.RequestMuteAsync(
                "echo-tts",
                cancellationToken);

            if (restoreMute)
            {
                SystemAudioController.SetMuted(
                    false);
            }

            if (volumeBoosted)
            {
                int appliedVolume =
                    SystemAudioController.SetVolumePercent(
                        boostedVolume);

                Console.WriteLine(
                    $"TTS -> volume boost {originalVolume}% -> {appliedVolume}%");
            }
            else
            {
                Console.WriteLine(
                    $"TTS -> volume already {originalVolume}%; no boost available");
            }

            TryShowActiveCall();

            await NeuralTtsSpeaker.SpeakAsync(
                text,
                cancellationToken);
        }
        finally
        {
            if (volumeBoosted)
            {
                int currentVolume =
                    SafeGetVolumePercent();

                if (Math.Abs(
                    currentVolume -
                    boostedVolume) <=
                    1)
                {
                    int restoredVolume =
                        SystemAudioController.SetVolumePercent(
                            originalVolume);

                    Console.WriteLine(
                        $"TTS -> volume restored to {restoredVolume}%");
                }
                else
                {
                    Console.WriteLine(
                        $"TTS -> volume changed externally to {currentVolume}%; leaving it alone");
                }
            }

            if (restoreMute)
            {
                SystemAudioController.SetMuted(
                    true);
            }

            await PhoneAudioSessionController.ReleaseMuteAsync(
                "echo-tts",
                CancellationToken.None);

            await RadioController.ReleasePauseAsync(
                "echo-tts",
                CancellationToken.None);

            lock (_gate)
            {
                _speaking =
                    false;
            }

            ttsWatch.Stop();

            DiagnosticLog.Event(
                "tts",
                "speak_complete",
                new
                {
                    latency_ms =
                        ttsWatch.ElapsedMilliseconds,
                    final_volume =
                        SafeGetVolumePercent(),
                    master_muted =
                        SafeGetMuted()
                });

            RefreshLights();
        }
    }

    private async Task BeginListeningDuckAsync(
        CancellationToken cancellationToken)
    {
        bool held =
            await RadioController.RequestPauseAsync(
                "echo-listen",
                cancellationToken);

        bool phoneHeld =
            await PhoneAudioSessionController.RequestMuteAsync(
                "echo-listen",
                cancellationToken);

        lock (_gate)
        {
            _listenDuckHeld =
                held ||
                phoneHeld;
        }

        DiagnosticLog.Event(
            "audio",
            "echo_listen_duck_acquired",
            new
            {
                radio =
                    held,
                phone =
                    phoneHeld,
                held =
                    _listenDuckHeld
            });
    }

    private async Task EndListeningDuckAsync()
    {
        bool release;

        lock (_gate)
        {
            release =
                _listenDuckHeld;

            _listenDuckHeld =
                false;
        }

        if (!release)
        {
            return;
        }

        await PhoneAudioSessionController.ReleaseMuteAsync(
            "echo-listen",
            CancellationToken.None);

        await RadioController.ReleasePauseAsync(
            "echo-listen",
            CancellationToken.None);

        DiagnosticLog.Event(
            "audio",
            "echo_listen_duck_released");
    }

    private void TrimRollingLocked(
        WaveFormat format)
    {
        int maxBytes =
            (int)(
                format.AverageBytesPerSecond *
                RollingDuration.TotalSeconds);

        maxBytes -=
            maxBytes %
            Math.Max(
                1,
                format.BlockAlign);

        if (_rolling.Length <=
            maxBytes)
        {
            return;
        }

        byte[] all =
            _rolling.ToArray();

        int take =
            Math.Min(
                maxBytes,
                all.Length);

        int start =
            all.Length -
            take;

        _rolling.Dispose();

        _rolling =
            new MemoryStream(
                maxBytes);

        _rolling.Write(
            all,
            start,
            take);
    }

    private void TryShowActiveCall()
    {
        try
        {
            _slice.Lights.ShowActiveCall();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"ASSISTANT LIGHTS -> unavailable: {ex.GetType().Name}: {ex.Message}");

            DiagnosticLog.Error(
                "lights",
                "assistant_indicator_failed",
                ex);
        }
    }

    private void RefreshLights()
    {
        try
        {
            lock (_gate)
            {
                if (
                    _listening ||
                    _processing ||
                    _speaking)
                {
                    TryShowActiveCall();
                    return;
                }
            }

            if (_recording.IsRecording)
            {
                if (_recording.IsPaused)
                {
                    _slice.Lights.ShowActiveMutedCall();
                }
                else
                {
                    TryShowActiveCall();
                }
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

    private string GetStateLocked()
    {
        if (!_enabled)
        {
            return "disabled";
        }

        if (_speaking)
        {
            return "speaking";
        }

        if (_processing)
        {
            return "processing";
        }

        if (_listening)
        {
            return "listening";
        }

        return "waiting-for-echo";
    }

    private static bool ContainsWakeWord(
        string text)
    {
        if (string.IsNullOrWhiteSpace(
            text))
        {
            return false;
        }

        string normalized =
            new string(
                text
                    .ToLowerInvariant()
                    .Select(
                        character =>
                            char.IsLetter(
                                character)
                                ? character
                                : ' ')
                    .ToArray());

        return normalized
            .Split(
                ' ',
                StringSplitOptions.RemoveEmptyEntries)
            .Any(
                word =>
                    word is
                        "echo" or
                        "eko");
    }

    private static string StripWakeWord(
        string transcript)
    {
        if (string.IsNullOrWhiteSpace(
            transcript))
        {
            return string.Empty;
        }

        string lower =
            transcript.ToLowerInvariant();

        int index =
            lower.IndexOf(
                "echo",
                StringComparison.Ordinal);

        int length =
            4;

        if (index < 0)
        {
            index =
                lower.IndexOf(
                    "eko",
                    StringComparison.Ordinal);

            length =
                3;
        }

        string result =
            index >= 0
                ? transcript[
                    (index + length)..]
                : transcript;

        return result
            .Trim(
                ' ',
                ',',
                '.',
                ':',
                ';',
                '-',
                '!',
                '?');
    }

    private static float MeasurePeak(
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

        int frameBytes =
            channels *
            bytesPerSample;

        if (frameBytes <= 0)
        {
            return 0;
        }

        WaveFormatExtensible? extensible =
            format as
                WaveFormatExtensible;

        bool float32 =
            format.BitsPerSample ==
                32 &&
            (
                format.Encoding ==
                    WaveFormatEncoding.IeeeFloat ||
                (
                    extensible is not null &&
                    extensible.SubFormat ==
                        AudioSubtypes.MFAudioFormat_Float
                )
            );

        bool pcm =
            format.Encoding ==
                WaveFormatEncoding.Pcm ||
            (
                extensible is not null &&
                extensible.SubFormat ==
                    AudioSubtypes.MFAudioFormat_PCM
            );

        float peak =
            0;

        for (int offset = 0;
             offset + bytesPerSample <=
                 bytesRecorded;
             offset +=
                 frameBytes)
        {
            float sample;

            if (float32)
            {
                sample =
                    Math.Abs(
                        BitConverter.ToSingle(
                            buffer,
                            offset));
            }
            else if (
                pcm &&
                format.BitsPerSample ==
                    16)
            {
                sample =
                    Math.Abs(
                        BitConverter.ToInt16(
                            buffer,
                            offset) /
                        32768f);
            }
            else
            {
                return 0;
            }

            if (sample >
                peak)
            {
                peak =
                    sample;
            }
        }

        return peak;
    }

    private static int SafeGetVolumePercent()
    {
        try
        {
            return SystemAudioController.VolumePercent;
        }
        catch
        {
            return 0;
        }
    }

    private static bool SafeGetMuted()
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

    public async ValueTask DisposeAsync()
    {
        SetEnabled(
            false);

        await EndListeningDuckAsync();

        _rolling.Dispose();
        _command.Dispose();
        _remoteWhisper.Dispose();
        await _wakeWhisper.DisposeAsync();
        await _fallbackWhisper.DisposeAsync();
        _hermes.Dispose();
    }
}
