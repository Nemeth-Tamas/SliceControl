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

    string? apiKey =
        noTranscription
            ? null
            : Environment.GetEnvironmentVariable(
                "OPENAI_API_KEY");

    SliceDevice slice =
        SliceDevice.Open();

    MMDevice microphone =
        MicrophoneSelector.Resolve(
            requestedMic);

    await using var recorder =
        new AudioRecorder(
            microphone,
            outputDirectory);

    await using var transcription =
        new LiveTranscriptionController(
            recorder,
            apiKey);

    using var cts =
        new CancellationTokenSource();

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

    try
    {
        if (restoreHpService)
        {
            Console.WriteLine(
                "Taking ownership of the Collaboration Cover...");

            HpTelephonyService.StopAndWait();
        }

        slice.Lights.Reset();

        Console.WriteLine(
            $"Microphone: {recorder.DeviceName}");

        Console.WriteLine(
            $"Recordings: {outputDirectory}");

        if (transcription.Enabled)
        {
            Console.WriteLine(
                "Live transcription: OpenAI gpt-live-transcribe, Hungarian");
        }
        else if (noTranscription)
        {
            Console.WriteLine(
                "Live transcription: disabled by --no-transcribe");
        }
        else
        {
            Console.WriteLine(
                "Live transcription: disabled - OPENAI_API_KEY is not set");
        }

        Console.WriteLine();
        Console.WriteLine(
            "GREEN  = start recording + transcription");

        Console.WriteLine(
            "MUTE   = pause/resume");

        Console.WriteLine(
            "RED    = stop + finalize WAV/TXT");

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
    LiveTranscriptionController transcription,
    ChannelReader<SlicePhysicalButtonEvent> reader,
    CancellationToken cancellationToken)
{
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

                string path =
                    recorder.Start();

                slice.Lights.EnterCallAnimation();

                await Task.Delay(
                    120,
                    cancellationToken);

                slice.Lights.ShowActiveCall();

                Console.WriteLine(
                    $"RECORDING -> {path}");

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

                        Console.Write(
                            "LIVE -> ");
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine(
                            $"Could not start live transcription: {ex.Message}");

                        Console.Error.WriteLine(
                            "Recording will continue without transcription.");
                    }
                }

                break;

            case SlicePhysicalButton.Hangup:
                if (!recorder.IsRecording)
                {
                    Console.WriteLine(
                        "Nothing to stop.");

                    slice.Lights.Reset();
                    break;
                }

                string? saved =
                    await recorder.StopAsync(
                        cancellationToken);

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
                    Console.Error.WriteLine();
                    Console.Error.WriteLine(
                        $"Could not finalize transcription: {ex.Message}");
                }

                slice.Lights.ExitAnimation();

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

                    Console.WriteLine();
                    Console.WriteLine(
                        "PAUSED");

                    try
                    {
                        await transcription.CommitAsync(
                            cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine(
                            $"Could not finalize transcript segment: {ex.Message}");
                    }
                }
                else
                {
                    slice.Lights.ShowActiveCall();

                    Console.WriteLine(
                        "RECORDING");

                    if (transcription.Enabled)
                    {
                        Console.Write(
                            "LIVE -> ");
                    }
                }

                break;

            case SlicePhysicalButton.VolumeDown:
                Console.WriteLine(
                    "VolumeDown (reserved for app control)");
                break;

            case SlicePhysicalButton.VolumeUp:
                Console.WriteLine(
                    "VolumeUp (reserved for app control)");
                break;

            default:
                Console.WriteLine(
                    $"Unknown Slice input: {FormatEvidence(ev)}");
                break;
        }
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
  SliceTranscribe run --no-transcribe
  SliceTranscribe mics

Environment:

  OPENAI_API_KEY
      Enables live Hungarian transcription with gpt-live-transcribe.

Controls:

  Pickup -> start WAV recording + live transcription
  Mute   -> pause/resume; pausing also commits the current transcript turn
  Hangup -> stop and finalize WAV + TXT transcript

The original WAV remains the source-of-truth recording. Final transcript turns
are appended to a UTF-8 .txt file beside the WAV.
""");
}
