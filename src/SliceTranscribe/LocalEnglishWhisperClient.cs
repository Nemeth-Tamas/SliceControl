using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System.Diagnostics;
using System.Text;
using Whisper.net;
using Whisper.net.Ggml;

namespace SliceTranscribe;

internal sealed record LocalWhisperResult(
    string Text,
    long ElapsedMilliseconds);

internal sealed class LocalEnglishWhisperClient :
    IAsyncDisposable
{
    private const int TargetSampleRate =
        16000;

    private const int VadFrameMilliseconds =
        20;

    private const float VadRmsThreshold =
        0.0055f;

    private const float VadPeakThreshold =
        0.016f;

    private const int VadMinimumSpeechFrames =
        2;

    private readonly SemaphoreSlim _gate =
        new(
            1,
            1);

    private WhisperFactory? _factory;
    private WhisperProcessor? _processor;

    private bool _disposed;

    public static string ModelPath =>
        Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "SliceAppliance",
            "STT",
            "ggml-tiny.en.bin");

    public async Task WarmUpAsync(
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
                $"LOCAL STT -> tiny.en warm-up failed: {ex.Message}");
        }
    }

    public async Task<LocalWhisperResult> TranscribeAsync(
        byte[] sourceAudio,
        WaveFormat captureFormat,
        string prompt,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        WaveFormat sourceFormat =
            captureFormat is WaveFormatExtensible extensible
                ? extensible.ToStandardWaveFormat()
                : captureFormat;

        float[] monoSamples =
            ResampleToMono16k(
                sourceAudio,
                sourceFormat);

        if (!ContainsSpeech(
            monoSamples))
        {
            return new LocalWhisperResult(
                Text:
                    string.Empty,
                ElapsedMilliseconds:
                    0);
        }

        await _gate.WaitAsync(
            cancellationToken);

        try
        {
            WhisperProcessor processor =
                await EnsureLoadedAsync(
                    cancellationToken,
                    gateAlreadyHeld: true);

            byte[] pcm16 =
                ConvertToPcm16(
                    monoSamples);

            using var pcmStream =
                new MemoryStream(
                    pcm16,
                    writable: false);

            using var pcmSource =
                new RawSourceWaveStream(
                    pcmStream,
                    new WaveFormat(
                        TargetSampleRate,
                        16,
                        1));

            using var wavStream =
                new MemoryStream();

            WaveFileWriter.WriteWavFileToStream(
                wavStream,
                pcmSource);

            wavStream.Position =
                0;

            var watch =
                Stopwatch.StartNew();

            var text =
                new StringBuilder();

            await foreach (
                var segment
                in processor.ProcessAsync(
                    wavStream,
                    cancellationToken))
            {
                string cleaned =
                    segment.Text.Trim();

                if (cleaned.Length == 0)
                {
                    continue;
                }

                if (text.Length > 0)
                {
                    text.Append(' ');
                }

                text.Append(
                    cleaned);
            }

            watch.Stop();

            return new LocalWhisperResult(
                Text:
                    text.ToString()
                        .Trim(),
                ElapsedMilliseconds:
                    watch.ElapsedMilliseconds);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<WhisperProcessor> EnsureLoadedAsync(
        CancellationToken cancellationToken,
        bool gateAlreadyHeld = false)
    {
        if (_processor is not null)
        {
            return _processor;
        }

        if (!gateAlreadyHeld)
        {
            await _gate.WaitAsync(
                cancellationToken);
        }

        try
        {
            if (_processor is not null)
            {
                return _processor;
            }

            await EnsureModelAsync(
                cancellationToken);

            Console.WriteLine(
                $"LOCAL STT -> loading tiny.en from {ModelPath}");

            _factory =
                WhisperFactory.FromPath(
                    ModelPath);

            _processor =
                _factory
                    .CreateBuilder()
                    .WithLanguage(
                        "en")
                    .WithPrompt(
                        "Echo. Calendar. Schedule. Client. Appointment. Reminder. Email. Radio. Cellnet.")
                    .WithNoContext()
                    .WithTemperature(
                        0.0f)
                    .WithNoSpeechThreshold(
                        0.55f)
                    .Build();

            // Force the first real transcription to pay as little setup cost
            // as possible. Model creation itself performs the heavy load.
            Console.WriteLine(
                "LOCAL STT -> tiny.en ready on Slice CPU");

            return _processor;
        }
        finally
        {
            if (!gateAlreadyHeld)
            {
                _gate.Release();
            }
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

        string? directory =
            Path.GetDirectoryName(
                ModelPath);

        if (!string.IsNullOrWhiteSpace(
            directory))
        {
            Directory.CreateDirectory(
                directory);
        }

        string partialPath =
            ModelPath +
            ".download";

        TryDelete(
            partialPath);

        Console.WriteLine(
            "LOCAL STT -> downloading Whisper tiny.en model once...");

        try
        {
            using Stream modelStream =
                await WhisperGgmlDownloader
                    .Default
                    .GetGgmlModelAsync(
                        GgmlType.TinyEn);

            await using (
                var file =
                    new FileStream(
                        partialPath,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None,
                        bufferSize:
                            1024 *
                            1024,
                        useAsync:
                            true))
            {
                await modelStream.CopyToAsync(
                    file,
                    cancellationToken);

                await file.FlushAsync(
                    cancellationToken);
            }

            File.Move(
                partialPath,
                ModelPath,
                overwrite: true);

            Console.WriteLine(
                $"LOCAL STT -> tiny.en model ready: {ModelPath}");
        }
        catch
        {
            TryDelete(
                partialPath);

            throw;
        }
    }

    private static float[] ResampleToMono16k(
        byte[] sourceAudio,
        WaveFormat sourceFormat)
    {
        using var rawStream =
            new MemoryStream(
                sourceAudio,
                writable: false);

        using var rawSource =
            new RawSourceWaveStream(
                rawStream,
                sourceFormat);

        ISampleProvider samples =
            rawSource.ToSampleProvider();

        if (samples.WaveFormat.Channels >
            1)
        {
            samples =
                new FixedChannelSampleProvider(
                    samples,
                    0);
        }

        var resampler =
            new WdlResamplingSampleProvider(
                samples,
                TargetSampleRate);

        var output =
            new List<float>(
                Math.Max(
                    TargetSampleRate,
                    sourceAudio.Length /
                    Math.Max(
                        1,
                        sourceFormat.BlockAlign)));

        float[] buffer =
            new float[4096];

        while (true)
        {
            int read =
                resampler.Read(
                    buffer,
                    0,
                    buffer.Length);

            if (read <= 0)
            {
                break;
            }

            for (int i = 0;
                 i < read;
                 i++)
            {
                output.Add(
                    buffer[i]);
            }
        }

        return output.ToArray();
    }

    private static bool ContainsSpeech(
        float[] samples)
    {
        if (samples.Length == 0)
        {
            return false;
        }

        int frameSamples =
            TargetSampleRate *
            VadFrameMilliseconds /
            1000;

        int qualifyingFrames =
            0;

        for (int offset = 0;
             offset < samples.Length;
             offset += frameSamples)
        {
            int count =
                Math.Min(
                    frameSamples,
                    samples.Length -
                    offset);

            if (count <= 0)
            {
                break;
            }

            double sumSquares =
                0;

            float peak =
                0;

            for (int i = 0;
                 i < count;
                 i++)
            {
                float sample =
                    samples[
                        offset +
                        i];

                float magnitude =
                    Math.Abs(
                        sample);

                peak =
                    Math.Max(
                        peak,
                        magnitude);

                sumSquares +=
                    sample *
                    sample;
            }

            float rms =
                (float)Math.Sqrt(
                    sumSquares /
                    count);

            if (
                rms >=
                    VadRmsThreshold &&
                peak >=
                    VadPeakThreshold)
            {
                qualifyingFrames++;

                if (qualifyingFrames >=
                    VadMinimumSpeechFrames)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static byte[] ConvertToPcm16(
        float[] samples)
    {
        byte[] pcm =
            new byte[
                samples.Length *
                sizeof(short)];

        for (int i = 0;
             i < samples.Length;
             i++)
        {
            float sample =
                Math.Clamp(
                    samples[i],
                    -1.0f,
                    1.0f);

            short value =
                sample >= 0
                    ? (short)(
                        sample *
                        short.MaxValue)
                    : (short)(
                        sample *
                        -short.MinValue);

            int offset =
                i *
                sizeof(short);

            pcm[offset] =
                (byte)(
                    value &
                    0xFF);

            pcm[offset + 1] =
                (byte)(
                    (value >>
                     8) &
                    0xFF);
        }

        return pcm;
    }

    private static void TryDelete(
        string path)
    {
        try
        {
            if (File.Exists(
                path))
            {
                File.Delete(
                    path);
            }
        }
        catch
        {
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(
                nameof(
                    LocalEnglishWhisperClient));
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed =
            true;

        _processor?.Dispose();
        _factory?.Dispose();
        _gate.Dispose();

        _processor =
            null;

        _factory =
            null;

        return ValueTask.CompletedTask;
    }

    private sealed class FixedChannelSampleProvider :
        ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly int _selectedChannel;

        private float[] _sourceBuffer =
            Array.Empty<float>();

        public FixedChannelSampleProvider(
            ISampleProvider source,
            int selectedChannel)
        {
            _source =
                source;

            _selectedChannel =
                selectedChannel;

            WaveFormat =
                WaveFormat.CreateIeeeFloatWaveFormat(
                    source.WaveFormat.SampleRate,
                    1);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(
            float[] buffer,
            int offset,
            int count)
        {
            int channels =
                _source.WaveFormat.Channels;

            int sourceSamplesNeeded =
                count *
                channels;

            if (_sourceBuffer.Length <
                sourceSamplesNeeded)
            {
                _sourceBuffer =
                    new float[
                        sourceSamplesNeeded];
            }

            int sourceSamplesRead =
                _source.Read(
                    _sourceBuffer,
                    0,
                    sourceSamplesNeeded);

            int frames =
                sourceSamplesRead /
                channels;

            for (int frame = 0;
                 frame < frames;
                 frame++)
            {
                buffer[
                    offset +
                    frame] =
                    _sourceBuffer[
                        frame *
                            channels +
                        _selectedChannel];
            }

            return frames;
        }
    }
}
