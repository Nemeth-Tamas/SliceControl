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
    string? LastWakeTranscript,
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
            1.2);

    private static readonly TimeSpan MaximumListenDuration =
        TimeSpan.FromSeconds(
            15);

    private const float SpeechPeakThreshold =
        0.018f;

    private readonly object _gate =
        new();

    private readonly SliceDevice _slice;
    private readonly RecordingCoordinator _recording;
    private readonly string? _microphoneName;
    private readonly WhisperOneShotClient _whisper;
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

    private DateTimeOffset _nextProbe =
        DateTimeOffset.MinValue;

    private DateTimeOffset _listeningStarted;
    private DateTimeOffset _lastSpeech;

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

        _whisper =
            new WhisperOneShotClient(
                whisperUrl);
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
                LastWakeTranscript:
                    _lastWakeTranscript,
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
    }

    public async Task RunAsync(
        CancellationToken cancellationToken)
    {
        _runCancellationToken =
            cancellationToken;

        _microphone =
            MicrophoneSelector.Resolve(
                _microphoneName);

        _capture =
            new WasapiCapture(
                _microphone);

        _captureFormat =
            _capture.WaveFormat;

        _capture.DataAvailable +=
            OnDataAvailable;

        Console.WriteLine(
            $"ASSISTANT -> ECHO wake mode on {_microphone.FriendlyName}");

        Console.WriteLine(
            "ASSISTANT -> v0 wake detection uses short English Whisper probes on the RTX 3090");

        Console.WriteLine(
            _hermes.IsConfigured
                ? $"ASSISTANT -> Hermes ready; session key {_hermes.SessionKey}"
                : $"ASSISTANT -> Hermes not configured; run Configure-SliceAssistant.ps1 ({_hermes.ConfigPath})");

        try
        {
            _capture.StartRecording();

            await Task.Delay(
                Timeout.InfiniteTimeSpan,
                cancellationToken);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            try
            {
                _capture.StopRecording();
            }
            catch
            {
            }

            _capture.DataAvailable -=
                OnDataAvailable;

            await EndListeningDuckAsync();

            _capture.Dispose();
            _microphone.Dispose();

            _capture =
                null;

            _microphone =
                null;
        }
    }

    private void OnDataAvailable(
        object? sender,
        WaveInEventArgs e)
    {
        WaveFormat? format =
            _captureFormat;

        if (format is null)
        {
            return;
        }

        byte[]? wakeSnapshot =
            null;

        byte[]? commandSnapshot =
            null;

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

                float peak =
                    MeasurePeak(
                        e.Buffer,
                        e.BytesRecorded,
                        format);

                if (peak >=
                    SpeechPeakThreshold)
                {
                    _lastSpeech =
                        now;
                }

                TimeSpan listenAge =
                    now -
                    _listeningStarted;

                bool silenceFinished =
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

                if (
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
            string text =
                await _whisper.TranscribeAsync(
                    audio,
                    format,
                    language: "en",
                    prompt:
                        "Echo. Calendar. Schedule. Client. Appointment. Reminder.",
                    cancellationToken);

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
                    $"ASSISTANT WAKE -> {text}");

                await BeginListeningDuckAsync(
                    cancellationToken);

                _slice.Lights.ShowActiveCall();
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
            string transcript =
                await _whisper.TranscribeAsync(
                    audio,
                    format,
                    language: "en",
                    prompt:
                        "Echo. Calendar. Schedule. Client. Appointment. Reminder. Cellnet.",
                    cancellationToken);

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

            if (!_hermes.IsConfigured)
            {
                throw new InvalidOperationException(
                    $"Hermes is not configured. Run Configure-SliceAssistant.ps1. Config: {_hermes.ConfigPath}");
            }

            string reply =
                await _hermes.SendAsync(
                    command,
                    cancellationToken);

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

            _slice.Lights.ShowActiveCall();

            await WindowsTtsSpeaker.SpeakAsync(
                text,
                cancellationToken);
        }
        finally
        {
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
                    _slice.Lights.ShowActiveCall();
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
                    _slice.Lights.ShowActiveCall();
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
        _whisper.Dispose();
        _hermes.Dispose();
    }
}
