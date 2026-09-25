using NAudio.CoreAudioApi;
using SliceControl;
using SliceTranscribe;
using System.Threading.Channels;

try
{
    string command =
        args.Length == 0
            ? "run"
            : args[0].ToLowerInvariant();

    if (command is "mics" or "microphones")
    {
        MicrophoneSelector.PrintActiveMicrophones();
        return 0;
    }

    if (command == "phone")
    {
        string phoneCommand =
            args.Length >= 2
                ? args[1].ToLowerInvariant()
                : "list";

        if (phoneCommand == "list")
        {
            return await PhoneAudioProbe.ListAsync();
        }

        if (phoneCommand == "connect")
        {
            string? requestedPhoneName =
                ReadOption(
                    args,
                    "--name");

            using var phoneCts =
                new CancellationTokenSource();

            return await PhoneAudioProbe.ConnectAsync(
                requestedPhoneName,
                phoneCts.Token);
        }

        if (phoneCommand == "playpause")
        {
            return PhoneLinkController.TryToggleMediaPlayback()
                ? 0
                : 1;
        }

        throw new ArgumentException(
            "phone must be 'list', 'connect', or 'playpause'.");
    }

    if (command == "calls")
    {
        string callsCommand =
            args.Length >= 2
                ? args[1].ToLowerInvariant()
                : "help";

        if (callsCommand == "record")
        {
            string? callMic =
                ReadOption(
                    args,
                    "--mic");

            using var callCts =
                new CancellationTokenSource();

            return await CallRecordingCommand.RunAsync(
                callMic,
                callCts.Token);
        }

        return callsCommand switch
        {
            "mute" =>
                PhoneLinkController.TryToggleMute()
                    ? 0
                    : 1,

            "hangup" =>
                PhoneLinkController.TryHangUp()
                    ? 0
                    : 1,

            _ =>
                throw new ArgumentException(
                    "calls must be 'mute', 'hangup', or 'record'.")
        };
    }

    if (command == "media")
    {
        string mediaCommand =
            args.Length >= 2
                ? args[1].ToLowerInvariant()
                : "list";

        if (mediaCommand == "list")
        {
            return await MediaControlProbe.ListAsync();
        }

        throw new ArgumentException(
            "media currently supports only 'list'.");
    }

    if (command == "model")
    {
        string downloadedModelPath =
            await LocalWhisperModel.EnsureBaseAsync(
                ReadOption(
                    args,
                    "--model"));

        Console.WriteLine(
            $"Local Whisper model: {downloadedModelPath}");

        return 0;
    }

    if (command == "analyzechannels")
    {
        if (args.Length < 2)
        {
            throw new ArgumentException(
                "analyzechannels requires a WAV path.");
        }

        ChannelAnalyzer.Run(
            args[1]);

        return 0;
    }

    if (command == "probechannels")
    {
        if (args.Length < 2)
        {
            throw new ArgumentException(
                "probechannels requires a WAV path.");
        }

        string probeServer =
            ReadOption(
                args,
                "--remote-url")
            ?? "http://192.168.1.2:8765";

        await ChannelProbe.RunAsync(
            args[1],
            probeServer);

        return 0;
    }

    if (command == "finalize")
    {
        if (args.Length < 2)
        {
            throw new ArgumentException(
                "finalize requires a WAV path.");
        }

        string finalizeWhisperServer =
            ReadOption(
                args,
                "--remote-url")
            ?? "http://192.168.1.2:8765";

        string? finalizeDiarizationServer =
            HasFlag(
                args,
                "--no-diarization")
                ? null
                : ReadOption(
                    args,
                    "--diarization-url")
                  ?? "http://192.168.1.2:8766";

        string? finalPath =
            await WindowedConsensusRefiner.RefineAsync(
                args[1],
                finalizeWhisperServer,
                finalizeDiarizationServer);

        if (!string.IsNullOrWhiteSpace(
            finalPath))
        {
            Console.WriteLine(
                $"FINAL TRANSCRIPT -> {finalPath}");
        }

        return 0;
    }

    if (command is not "run")
    {
        PrintHelp();
        return 2;
    }

    string? requestedMic =
        ReadOption(
            args,
            "--mic");

    string outputDirectory =
        ReadOption(
            args,
            "--output")
        ?? Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.MyDocuments),
            "SliceTranscribe",
            "Recordings");

    bool noTranscription =
        HasFlag(
            args,
            "--no-transcribe");

    const string defaultRemoteUrl =
        "http://192.168.1.2:8765";

    const string defaultDiarizationUrl =
        "http://192.168.1.2:8766";

    string transcriptionMode =
        noTranscription
            ? "none"
            : (
                ReadOption(
                    args,
                    "--transcriber")
                ?? "remote")
                .ToLowerInvariant();

    if (transcriptionMode is not (
        "remote" or
        "local" or
        "openai" or
        "none"))
    {
        throw new ArgumentException(
            "--transcriber must be remote, local, openai, or none.");
    }

    string remoteUrl =
        ReadOption(
            args,
            "--remote-url")
        ?? defaultRemoteUrl;

    bool noDiarization =
        HasFlag(
            args,
            "--no-diarization");

    string? diarizationUrl =
        noDiarization
            ? null
            : ReadOption(
                args,
                "--diarization-url")
              ?? defaultDiarizationUrl;

    int remoteChannel =
        0;

    string? remoteChannelText =
        ReadOption(
            args,
            "--remote-channel");

    if (remoteChannelText is not null &&
        (!int.TryParse(
            remoteChannelText,
            out remoteChannel) ||
         remoteChannel < 0))
    {
        throw new ArgumentException(
            "--remote-channel must be zero or a positive integer.");
    }

    string? phoneName =
        ReadOption(
            args,
            "--phone-name");

    string? modelPath =
        null;

    string? apiKey =
        null;

    if (transcriptionMode == "local")
    {
        modelPath =
            await LocalWhisperModel.EnsureBaseAsync(
                ReadOption(
                    args,
                    "--model"));
    }
    else if (transcriptionMode == "openai")
    {
        apiKey =
            Environment.GetEnvironmentVariable(
                "OPENAI_API_KEY");

        if (string.IsNullOrWhiteSpace(
            apiKey))
        {
            throw new InvalidOperationException(
                "OPENAI_API_KEY is required when --transcriber openai is selected.");
        }
    }

    SliceDevice slice =
        SliceDevice.Open();

    MMDevice microphone =
        MicrophoneSelector.Resolve(
            requestedMic);

    await using var recorder =
        new AudioRecorder(
            microphone,
            outputDirectory);

    await using ITranscriptionController transcription =
        transcriptionMode switch
        {
            "remote" =>
                new RemoteWhisperTranscriptionController(
                    recorder,
                    remoteUrl,
                    remoteChannel,
                    diarizationUrl),

            "local" =>
                new LocalWhisperTranscriptionController(
                    recorder,
                    modelPath!),

            "openai" =>
                new LiveTranscriptionController(
                    recorder,
                    apiKey),

            _ =>
                new DisabledTranscriptionController()
        };

    using var cts =
        new CancellationTokenSource();

    await using var phoneAudio =
        new PhoneAudioManager(
            phoneName);

    var audioActivity =
        new AudioActivityMonitor();

    Console.CancelKeyPress +=
        (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cts.Cancel();
        };

    bool restoreHpService =
        HpTelephonyService.IsRunning();

    var buttonEvents =
        Channel.CreateUnbounded<
            SlicePhysicalButtonEvent>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = true
            });

    Task? buttonWatchTask =
        null;

    Task? phoneAudioTask =
        null;

    Task? audioActivityTask =
        null;

    try
    {
        if (restoreHpService)
        {
            Console.WriteLine(
                "Taking ownership of the Collaboration Cover...");

            HpTelephonyService.StopAndWait();
        }

        slice.Lights.Reset();

        phoneAudioTask =
            phoneAudio.RunAsync(
                cts.Token);

        audioActivityTask =
            audioActivity.RunAsync(
                cts.Token);

        Console.WriteLine(
            "Audio priority: active iPhone A2DP media mutes Retro Radio; 2 s quiet unmutes it");

        Console.WriteLine(
            "Audio runtime: stable A2DP-only baseline; call experiments are manual commands only");

        Console.WriteLine(
            phoneName is null
                ? "Phone audio: automatic A2DP source discovery/reconnect"
                : $"Phone audio: automatic A2DP reconnect for {phoneName}");

        Console.WriteLine(
            $"Microphone: {recorder.DeviceName}");

        Console.WriteLine(
            $"Recordings: {outputDirectory}");

        switch (transcriptionMode)
        {
            case "remote":
                Console.WriteLine(
                    "Transcription: remote whisper.cpp / large-v3 / Hungarian");

                Console.WriteLine(
                    $"Server: {remoteUrl}");

                Console.WriteLine(
                    $"Microphone channel: {remoteChannel}");

                Console.WriteLine(
                    diarizationUrl is null
                        ? "Diarization: disabled"
                        : $"Diarization: {diarizationUrl}");

                break;

            case "local":
                Console.WriteLine(
                    "Transcription: local Whisper multilingual base / CPU / Hungarian");

                Console.WriteLine(
                    $"Model: {modelPath}");

                break;

            case "openai":
                Console.WriteLine(
                    "Transcription: OpenAI gpt-live-transcribe / Hungarian");
                break;

            default:
                Console.WriteLine(
                    "Transcription: disabled");
                break;
        }

        Console.WriteLine();
        Console.WriteLine(
            "GREEN  = start recording + transcription");

        Console.WriteLine(
            "MUTE   = pause/resume");

        Console.WriteLine(
            "RED    = stop + finalize WAV/TXT");

        Console.WriteLine(
            "RED idle = toggle quiet/night mode (master output mute)");

        Console.WriteLine(
            "Ctrl+C = quit");

        Console.WriteLine();

        buttonWatchTask =
            PumpButtonsAsync(
                slice,
                buttonEvents.Writer,
                cts.Token);

        await RunButtonLoopAsync(
            slice,
            recorder,
            transcription,
            buttonEvents.Reader,
            cts.Token);
    }
    catch (OperationCanceledException)
        when (cts.IsCancellationRequested)
    {
    }
    finally
    {
        cts.Cancel();

        if (recorder.IsRecording)
        {
            try
            {
                string? saved =
                    await recorder.StopAsync();

                if (saved is not null)
                {
                    Console.WriteLine(
                        $"Saved: {saved}");
                }

                await RadioController.ReleasePauseAsync(
                    "recording",
                    CancellationToken.None);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"Could not finalize recording: {ex.Message}");
            }
        }

        try
        {
            string? transcript =
                await transcription.FinishAsync();

            if (transcript is not null)
            {
                Console.WriteLine(
                    $"Transcript: {transcript}");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"Could not finalize transcription: {ex.Message}");
        }

        try
        {
            slice.Lights.Reset();
        }
        catch
        {
        }

        if (buttonWatchTask is not null)
        {
            try
            {
                await buttonWatchTask;
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"Button monitor stopped with an error: {ex.Message}");
            }
        }

        if (phoneAudioTask is not null)
        {
            try
            {
                await phoneAudioTask;
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"Phone audio manager stopped with an error: {ex.Message}");
            }
        }

        if (audioActivityTask is not null)
        {
            try
            {
                await audioActivityTask;
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"Audio activity monitor stopped with an error: {ex.Message}");
            }
        }

        if (restoreHpService &&
            !HpTelephonyService.IsRunning())
        {
            Console.WriteLine(
                "Restoring HPSliceTelephonyService...");

            HpTelephonyService.StartAndWait();
        }
    }

    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine(
        $"{ex.GetType().Name}: {ex.Message}");

    return 1;
}

static async Task PumpButtonsAsync(
    SliceDevice slice,
    ChannelWriter<SlicePhysicalButtonEvent> writer,
    CancellationToken cancellationToken)
{
    try
    {
        await slice.WatchPhysicalButtonsAsync(
            ev =>
                writer.TryWrite(
                    ev),
            cancellationToken);

        writer.TryComplete();
    }
    catch (OperationCanceledException)
        when (cancellationToken.IsCancellationRequested)
    {
        writer.TryComplete();
    }
    catch (Exception ex)
    {
        writer.TryComplete(ex);
        throw;
    }
}

static async Task RunButtonLoopAsync(
    SliceDevice slice,
    AudioRecorder recorder,
    ITranscriptionController transcription,
    ChannelReader<SlicePhysicalButtonEvent> reader,
    CancellationToken cancellationToken)
{
    bool radioPausedByRecording =
        false;

    await foreach (
        SlicePhysicalButtonEvent ev
        in reader.ReadAllAsync(
            cancellationToken))
    {
        switch (ev.Button)
        {
            case SlicePhysicalButton.Pickup:
                if (recorder.IsRecording)
                {
                    Console.WriteLine(
                        "Already recording.");

                    break;
                }

                radioPausedByRecording =
                    await RadioController.RequestPauseAsync(
                        "recording",
                        cancellationToken);

                if (radioPausedByRecording)
                {
                    await Task.Delay(
                        100,
                        cancellationToken);
                }

                string path;

                try
                {
                    path =
                        recorder.Start();
                }
                catch
                {
                    if (radioPausedByRecording)
                    {
                        await RadioController.ReleasePauseAsync(
                            "recording",
                            CancellationToken.None);

                        radioPausedByRecording =
                            false;
                    }

                    throw;
                }

                slice.Lights.EnterCallAnimation();

                await Task.Delay(
                    120,
                    cancellationToken);

                slice.Lights.ShowActiveCall();

                Console.WriteLine(
                    $"RECORDING -> {path}");

                Console.WriteLine(
                    $"CAPTURE -> {recorder.CaptureFormat}");

                if (transcription.Enabled)
                {
                    try
                    {
                        string? transcriptPath =
                            await transcription.StartAsync(
                                path,
                                cancellationToken);

                        Console.WriteLine(
                            $"TRANSCRIBING -> {transcriptPath}");
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine(
                            $"Could not start transcription: {ex.Message}");

                        Console.Error.WriteLine(
                            "Recording will continue without transcription.");
                    }
                }

                break;

            case SlicePhysicalButton.Hangup:
                if (!recorder.IsRecording)
                {
                    bool quietMode =
                        SystemAudioController.ToggleMute();

                    Console.WriteLine(
                        quietMode
                            ? "QUIET MODE -> ON (master output muted)"
                            : "QUIET MODE -> OFF (master output unmuted)");

                    if (quietMode)
                    {
                        slice.Lights.SetMutedCall(
                            0);

                        await Task.Delay(
                            500,
                            cancellationToken);
                    }

                    slice.Lights.Reset();
                    break;
                }

                string? saved =
                    await recorder.StopAsync(
                        cancellationToken);

                slice.Lights.ExitAnimation();

                if (radioPausedByRecording)
                {
                    await RadioController.ReleasePauseAsync(
                        "recording",
                        cancellationToken);

                    radioPausedByRecording =
                        false;
                }

                string? transcript =
                    null;

                try
                {
                    transcript =
                        await transcription.FinishAsync(
                            cancellationToken);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine(
                        $"Could not finalize transcription: {ex.Message}");
                }

                Console.WriteLine();
                Console.WriteLine(
                    $"SAVED -> {saved}");

                if (transcript is not null)
                {
                    Console.WriteLine(
                        $"TRANSCRIPT -> {transcript}");
                }

                break;

            case SlicePhysicalButton.Mute:
                if (!recorder.IsRecording)
                {
                    Console.WriteLine(
                        "Mute ignored while idle.");

                    break;
                }

                bool paused =
                    recorder.TogglePause();

                if (paused)
                {
                    slice.Lights.ShowActiveMutedCall();

                    Console.WriteLine(
                        "PAUSED");

                    if (transcription.Enabled)
                    {
                        _ =
                            CommitTranscriptSegmentAsync(
                                transcription);
                    }
                }
                else
                {
                    slice.Lights.ShowActiveCall();

                    Console.WriteLine(
                        "RECORDING");
                }

                break;

            case SlicePhysicalButton.VolumeDown:
            case SlicePhysicalButton.VolumeUp:
                await ShowVolumeFeedbackAsync(
                    slice,
                    recorder,
                    ev.Button,
                    cancellationToken);
                break;

            default:
                Console.WriteLine(
                    $"Unknown Slice input: {FormatEvidence(ev)}");
                break;
        }
    }
}

static async Task ShowVolumeFeedbackAsync(
    SliceDevice slice,
    AudioRecorder recorder,
    SlicePhysicalButton button,
    CancellationToken cancellationToken)
{
    int volume = GetMasterPlaybackVolumePercent();

    if (recorder.IsRecording)
    {
        if (recorder.IsPaused)
        {
            slice.Lights.SetMutedCall(volume);
        }
        else
        {
            slice.Lights.SetCall(volume);
        }
    }
    else
    {
        slice.Lights.SetBar(volume);
    }

    Console.WriteLine(
        $"{button}: Windows volume {volume}%");

    await Task.Delay(
        700,
        cancellationToken);

    if (recorder.IsRecording)
    {
        if (recorder.IsPaused)
        {
            slice.Lights.ShowActiveMutedCall();
        }
        else
        {
            slice.Lights.ShowActiveCall();
        }
    }
    else
    {
        slice.Lights.Reset();
    }
}

static int GetMasterPlaybackVolumePercent()
{
    using var enumerator =
        new MMDeviceEnumerator();

    using MMDevice endpoint =
        enumerator.GetDefaultAudioEndpoint(
            DataFlow.Render,
            Role.Multimedia);

    return (int)Math.Round(
        endpoint.AudioEndpointVolume.MasterVolumeLevelScalar *
        100.0f);
}

static async Task CommitTranscriptSegmentAsync(
    ITranscriptionController transcription)
{
    try
    {
        await transcription.CommitAsync();
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(
            $"Could not finalize transcript segment: {ex.Message}");
    }
}

static string FormatEvidence(
    SlicePhysicalButtonEvent ev)
{
    return ev.Evidence.Count == 0
        ? "no HID evidence"
        : string.Join(
            " | ",
            ev.Evidence.Select(
                report => report.ToString()));
}

static string? ReadOption(
    string[] args,
    string name)
{
    for (int i = 1;
         i < args.Length;
         i++)
    {
        if (!string.Equals(
            args[i],
            name,
            StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        if (i + 1 >= args.Length)
        {
            throw new ArgumentException(
                $"Missing value for {name}.");
        }

        return args[i + 1];
    }

    return null;
}

static bool HasFlag(
    string[] args,
    string name)
{
    return args.Any(
        arg =>
            string.Equals(
                arg,
                name,
                StringComparison.OrdinalIgnoreCase));
}

static void PrintHelp()
{
    Console.WriteLine(
"""
SliceTranscribe

Usage:

  SliceTranscribe run
  SliceTranscribe run --mic "microphone name"
  SliceTranscribe run --output "C:\path\to\recordings"

  SliceTranscribe run --transcriber remote
  SliceTranscribe run --remote-url "http://192.168.1.2:8765"
  SliceTranscribe run --remote-channel 0
  SliceTranscribe run --diarization-url "http://192.168.1.2:8766"
  SliceTranscribe run --no-diarization
  SliceTranscribe run --transcriber local
  SliceTranscribe run --transcriber openai
  SliceTranscribe run --transcriber none
  SliceTranscribe run --no-transcribe
  SliceTranscribe run --phone-name "Tamás's iPhone"

  SliceTranscribe model
  SliceTranscribe model --model "C:\path\ggml-base.bin"
  SliceTranscribe analyzechannels "C:\path\recording.wav"
  SliceTranscribe probechannels "C:\path\recording.wav"
  SliceTranscribe probechannels "C:\path\recording.wav" --remote-url "http://192.168.1.2:8765"
  SliceTranscribe finalize "C:\path\recording.wav"
  SliceTranscribe finalize "C:\path\recording.wav" --no-diarization
  SliceTranscribe mics
  SliceTranscribe phone list
  SliceTranscribe phone connect
  SliceTranscribe phone connect --name "device name"
  SliceTranscribe phone playpause
  SliceTranscribe media list
  SliceTranscribe calls mute
  SliceTranscribe calls hangup
  SliceTranscribe calls record
  SliceTranscribe calls record --mic "HP Bang & Olufsen Audio Module"

Default transcription backend:

  remote
      whisper.cpp HTTP server at http://192.168.1.2:8765
      Intended for full large-v3 on the RTX 3090 home PC.
      Defaults to microphone channel 0 and 12-second live chunks.
      The HP B&O endpoint exposes six bit-identical capture channels, so final
      transcription uses channel 0 only in 12-second windows. Speaker
      diarization defaults to:
      http://192.168.1.2:8766

Optional backends:

  local
      Whisper multilingual base, CPU-only, Hungarian.
      The model is downloaded once to:
      %LOCALAPPDATA%\SliceTranscribe\Models\ggml-base.bin

  openai
      Uses gpt-live-transcribe and requires OPENAI_API_KEY.

Controls:

  Pickup -> start WAV recording + transcription
  Mute   -> pause/resume and flush the current local transcript chunk
  Hangup -> stop and finalize WAV + TXT transcript
  Hangup while idle -> toggle quiet/night mode (master output mute)

Phone audio:

  The run mode automatically discovers a paired Bluetooth A2DP source,
  opens it as a Windows audio sink, and reconnects after disconnects.
  Use --phone-name to prefer a specific paired device when needed.

The original WAV remains the source-of-truth recording. The remote Whisper
backend emits completed text roughly every twelve seconds; the local fallback
still uses shorter chunks. Text is appended to a UTF-8 .txt file beside the WAV.
""");
}
