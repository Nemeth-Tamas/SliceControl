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

    SliceDevice slice =
        SliceDevice.Open();

    MMDevice microphone =
        MicrophoneSelector.Resolve(
            requestedMic);

    await using var recorder =
        new AudioRecorder(
            microphone,
            outputDirectory);

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

        Console.WriteLine();
        Console.WriteLine(
            "GREEN  = start recording");

        Console.WriteLine(
            "MUTE   = pause/resume");

        Console.WriteLine(
            "RED    = stop + finalize WAV");

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
            slice.Telephony.EndCall();
        }
        catch
        {
            try
            {
                slice.Lights.Reset();
            }
            catch
            {
            }
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

                Console.WriteLine(
                    $"RECORDING -> {path}");

                slice.Telephony.EnterCall();

                await Task.Delay(
                    120,
                    cancellationToken);

                slice.Telephony.ActiveImmediateExit();
                break;

            case SlicePhysicalButton.Hangup:
                if (!recorder.IsRecording)
                {
                    Console.WriteLine(
                        "Nothing to stop.");

                    slice.Telephony.EndCall();
                    break;
                }

                string? saved =
                    await recorder.StopAsync(
                        cancellationToken);

                slice.Telephony.EndCall();

                Console.WriteLine(
                    $"SAVED -> {saved}");

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
                    slice.Telephony.ApplyMuteTheme(
                        SliceTelephonyState.ActiveImmediateExit);

                    Console.WriteLine(
                        "PAUSED");
                }
                else
                {
                    slice.Telephony.ClearMuteTheme(
                        SliceTelephonyState.ActiveImmediateExit);

                    Console.WriteLine(
                        "RECORDING");
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

static void PrintHelp()
{
    Console.WriteLine(
"""
SliceTranscribe

Usage:

  SliceTranscribe run
  SliceTranscribe run --mic "microphone name"
  SliceTranscribe run --output "C:\path\to\recordings"
  SliceTranscribe mics

Current milestone:
  Pickup -> start WAV recording
  Mute   -> pause/resume recording
  Hangup -> stop and finalize WAV

Hungarian speech-to-text is the next layer after this hardware/audio lifecycle
has been validated on the HP Elite Slice.
""");
}
