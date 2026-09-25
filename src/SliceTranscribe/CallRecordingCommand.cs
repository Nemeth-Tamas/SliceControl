using NAudio.CoreAudioApi;

namespace SliceTranscribe;

internal static class CallRecordingCommand
{
    public static async Task<int> RunAsync(
        string? requestedMic,
        CancellationToken cancellationToken)
    {
        using var enumerator =
            new MMDeviceEnumerator();

        MMDevice renderDevice =
            enumerator.GetDefaultAudioEndpoint(
                DataFlow.Render,
                Role.Communications);

        MMDevice microphoneDevice =
            MicrophoneSelector.Resolve(
                requestedMic);

        string outputDirectory =
            Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.MyDocuments),
                "SliceTranscribe",
                "Calls");

        await using var recorder =
            new CallRecorder(
                renderDevice,
                microphoneDevice,
                outputDirectory);

        Console.WriteLine(
            $"Call output : {recorder.RenderDeviceName}");

        Console.WriteLine(
            $"Call mic    : {recorder.MicrophoneDeviceName}");

        Console.WriteLine(
            $"Call files  : {outputDirectory}");

        recorder.Start();

        Console.WriteLine();
        Console.WriteLine(
            $"REMOTE -> {recorder.RemotePath}");

        Console.WriteLine(
            $"LOCAL  -> {recorder.LocalPath}");

        Console.WriteLine();
        Console.WriteLine(
            "Recording both sides. Press Ctrl+C to stop.");

        var completion =
            new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);

        ConsoleCancelEventHandler handler =
            (_, eventArgs) =>
            {
                eventArgs.Cancel =
                    true;

                completion.TrySetResult(
                    true);
            };

        Console.CancelKeyPress +=
            handler;

        try
        {
            using CancellationTokenRegistration registration =
                cancellationToken.Register(
                    () =>
                        completion.TrySetCanceled(
                            cancellationToken));

            await completion.Task;
        }
        finally
        {
            Console.CancelKeyPress -=
                handler;

            await recorder.StopAsync(
                CancellationToken.None);
        }

        Console.WriteLine(
            "Call recording stopped.");

        return 0;
    }
}
