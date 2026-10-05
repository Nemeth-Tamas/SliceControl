using KokoroSharp;
using KokoroSharp.Core;
using NAudio.Wave;

namespace SliceTranscribe;

internal static class NeuralTtsSpeaker
{
    private const string VoiceName =
        "af_heart";

    private const string ModelUrl =
        "https://github.com/Lyrcaxis/KokoroSharpBinaries/releases/download/v2.0.0/kokoro.onnx";

    private static readonly SemaphoreSlim Gate =
        new(
            1,
            1);

    private static readonly HttpClient Http =
        new()
        {
            Timeout =
                TimeSpan.FromSeconds(
                    60)
        };

    private static KokoroWavSynthesizer? _synthesizer;

    private static string ModelDirectory =>
        Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "SliceAppliance",
            "TTS");

    private static string ModelPath =>
        Path.Combine(
            ModelDirectory,
            "kokoro.onnx");

    public static async Task WarmUpAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            await EnsureLoadedAsync(
                cancellationToken);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"TTS -> Kokoro warm-up failed: {ex.Message}");
        }
    }

    public static async Task SpeakAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(
            text))
        {
            return;
        }

        if (!File.Exists(
            ModelPath))
        {
            Console.Error.WriteLine(
                "TTS -> Kokoro model is not installed yet; using Windows SAPI without waiting for download");

            DiagnosticLog.Warning(
                "tts",
                "kokoro_model_missing_sapi_fallback");

            await WindowsTtsSpeaker.SpeakAsync(
                text,
                cancellationToken);

            return;
        }

        using var interactiveTimeout =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);

        interactiveTimeout.CancelAfter(
            TimeSpan.FromSeconds(
                20));

        try
        {
            KokoroWavSynthesizer synthesizer =
                await EnsureLoadedAsync(
                    interactiveTimeout.Token);

            KokoroVoice voice =
                KokoroVoiceManager.GetVoice(
                    VoiceName);

            byte[] pcm =
                await synthesizer.SynthesizeAsync(
                    text,
                    voice);

            interactiveTimeout.Token.ThrowIfCancellationRequested();

            await PlayPcmAsync(
                pcm,
                interactiveTimeout.Token);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
            when (interactiveTimeout.IsCancellationRequested)
        {
            Console.Error.WriteLine(
                "TTS -> Kokoro interactive timeout; falling back to Windows SAPI");

            DiagnosticLog.Warning(
                "tts",
                "kokoro_interactive_timeout");

            await WindowsTtsSpeaker.SpeakAsync(
                text,
                cancellationToken);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"TTS -> Kokoro failed, falling back to Windows SAPI: {ex.Message}");

            DiagnosticLog.Error(
                "tts",
                "kokoro_failed",
                ex);

            await WindowsTtsSpeaker.SpeakAsync(
                text,
                cancellationToken);
        }
    }

    private static async Task<KokoroWavSynthesizer> EnsureLoadedAsync(
        CancellationToken cancellationToken)
    {
        if (_synthesizer is not null)
        {
            return _synthesizer;
        }

        await Gate.WaitAsync(
            cancellationToken);

        try
        {
            if (_synthesizer is not null)
            {
                return _synthesizer;
            }

            Directory.CreateDirectory(
                ModelDirectory);

            await EnsureModelAsync(
                cancellationToken);

            Console.WriteLine(
                $"TTS -> loading Kokoro model from {ModelPath}");

            _synthesizer =
                await Task.Run(
                    () =>
                        KokoroWavSynthesizer.LoadModel(
                            ModelPath),
                    cancellationToken);

            _ =
                KokoroVoiceManager.GetVoice(
                    VoiceName);

            Console.WriteLine(
                $"TTS -> Kokoro ready ({VoiceName})");

            return _synthesizer;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task EnsureModelAsync(
        CancellationToken cancellationToken)
    {
        if (File.Exists(
            ModelPath))
        {
            return;
        }

        string temporaryPath =
            ModelPath +
            ".download";

        Console.WriteLine(
            "TTS -> downloading Kokoro neural voice model (~320 MB, one time only)...");

        using HttpResponseMessage response =
            await Http.GetAsync(
                ModelUrl,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

        response.EnsureSuccessStatusCode();

        await using Stream source =
            await response.Content.ReadAsStreamAsync(
                cancellationToken);

        await using (
            var target =
                new FileStream(
                    temporaryPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 1024 * 1024,
                    useAsync: true))
        {
            await source.CopyToAsync(
                target,
                1024 * 1024,
                cancellationToken);
        }

        File.Move(
            temporaryPath,
            ModelPath,
            overwrite: true);

        Console.WriteLine(
            "TTS -> Kokoro model download complete");
    }

    private static async Task PlayPcmAsync(
        byte[] pcm,
        CancellationToken cancellationToken)
    {
        using var stream =
            new MemoryStream(
                pcm,
                writable: false);

        using var source =
            new RawSourceWaveStream(
                stream,
                new WaveFormat(
                    24000,
                    16,
                    1));

        using var output =
            new WaveOutEvent();

        var completion =
            new TaskCompletionSource<StoppedEventArgs>(
                TaskCreationOptions.RunContinuationsAsynchronously);

        void OnStopped(
            object? sender,
            StoppedEventArgs args)
        {
            completion.TrySetResult(
                args);
        }

        output.PlaybackStopped +=
            OnStopped;

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

        try
        {
            output.Init(
                source);

            output.Play();

            StoppedEventArgs stopped =
                await completion.Task.WaitAsync(
                    cancellationToken);

            if (stopped.Exception is not null)
            {
                throw stopped.Exception;
            }
        }
        finally
        {
            output.PlaybackStopped -=
                OnStopped;
        }
    }
}
