using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System.Text;
using System.Threading.Channels;
using Whisper.net;

namespace SliceTranscribe;

internal sealed class LocalWhisperTranscriptionController :
    ITranscriptionController
{
    private const int TargetSampleRate =
        16000;

    private static readonly TimeSpan DefaultChunkDuration =
        TimeSpan.FromSeconds(6);

    private static readonly TimeSpan MinimumFlushDuration =
        TimeSpan.FromMilliseconds(500);

    private const string Prompt =
        "Cellnet, Dunaújváros, iPhone, Android, Windows, Samsung, Xiaomi, USB, SSD, RAM, Wi-Fi.";

    private const int VadFrameMilliseconds =
        20;

    private const float VadRmsThreshold =
        0.006f;

    private const float VadPeakThreshold =
        0.018f;

    private const int VadMinimumSpeechFrames =
        3;

    private readonly object _gate =
        new();

    private readonly AudioRecorder _recorder;
    private readonly string _modelPath;
    private readonly TimeSpan _chunkDuration;

    private readonly List<byte[]> _startupBuffer =
        new();

    private Channel<LocalCommand>? _commands;
    private Task? _workerTask;

    private WhisperFactory? _factory;
    private WhisperProcessor? _processor;

    private WaveFormat? _sourceFormat;

    private string? _transcriptPath;

    private bool _starting;
    private bool _disposed;

    public LocalWhisperTranscriptionController(
        AudioRecorder recorder,
        string modelPath,
        TimeSpan? chunkDuration = null)
    {
        _recorder = recorder;
        _modelPath = modelPath;
        _chunkDuration =
            chunkDuration ??
            DefaultChunkDuration;

        if (_chunkDuration <
            TimeSpan.FromSeconds(2))
        {
            throw new ArgumentOutOfRangeException(
                nameof(chunkDuration),
                "Local Whisper chunks must be at least 2 seconds.");
        }

        _recorder.AudioChunkAvailable +=
            OnAudioChunkAvailable;
    }

    public bool Enabled => true;

    public string? TranscriptPath =>
        _transcriptPath;

    public async Task<string?> StartAsync(
        string wavPath,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        WaveFormat captureFormat =
            _recorder.CaptureFormat
            ?? throw new InvalidOperationException(
                "Microphone capture format is not available.");

        WaveFormat sourceFormat =
            captureFormat is WaveFormatExtensible extensible
                ? extensible.ToStandardWaveFormat()
                : captureFormat;

        lock (_gate)
        {
            if (_commands is not null ||
                _starting)
            {
                throw new InvalidOperationException(
                    "A local Whisper transcription session is already active.");
            }

            _starting = true;
            _startupBuffer.Clear();
        }

        string transcriptPath =
            Path.ChangeExtension(
                wavPath,
                ".txt");

        try
        {
            await WriteTranscriptHeaderAsync(
                transcriptPath,
                cancellationToken);

            WhisperFactory factory =
                WhisperFactory.FromPath(
                    _modelPath);

            WhisperProcessor processor =
                factory
                    .CreateBuilder()
                    .WithLanguage("hu")
                    .WithPrompt(Prompt)
                    .WithNoContext()
                    .WithTemperature(0.0f)
                    .WithNoSpeechThreshold(0.45f)
                    .Build();

            Channel<LocalCommand> commands =
                Channel.CreateUnbounded<LocalCommand>(
                    new UnboundedChannelOptions
                    {
                        SingleReader = true,
                        SingleWriter = false
                    });

            Task workerTask =
                Task.Run(
                    () =>
                        WorkerAsync(
                            commands.Reader,
                            sourceFormat,
                            processor,
                            transcriptPath,
                            cancellationToken),
                    CancellationToken.None);

            lock (_gate)
            {
                _factory = factory;
                _processor = processor;
                _sourceFormat = sourceFormat;
                _transcriptPath = transcriptPath;
                _commands = commands;
                _workerTask = workerTask;

                foreach (
                    byte[] chunk
                    in _startupBuffer)
                {
                    commands.Writer.TryWrite(
                        new AudioDataCommand(
                            chunk));
                }

                _startupBuffer.Clear();
                _starting = false;
            }

            Console.WriteLine(
                $"Local Whisper ready: base / hu / {_chunkDuration.TotalSeconds:0.#} s chunks");

            return transcriptPath;
        }
        catch
        {
            lock (_gate)
            {
                _starting = false;
                _startupBuffer.Clear();
            }

            await CleanupSessionAsync();
            throw;
        }
    }

    public async Task<string?> CommitAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        Channel<LocalCommand>? commands;

        lock (_gate)
        {
            commands =
                _commands;
        }

        if (commands is null)
        {
            return null;
        }

        var completion =
            new TaskCompletionSource<string?>(
                TaskCreationOptions.RunContinuationsAsynchronously);

        await commands.Writer.WriteAsync(
            new CommitCommand(
                completion),
            cancellationToken);

        return await completion.Task.WaitAsync(
            cancellationToken);
    }

    public async Task<string?> FinishAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        Channel<LocalCommand>? commands;
        Task? workerTask;
        string? transcriptPath;

        lock (_gate)
        {
            commands =
                _commands;

            workerTask =
                _workerTask;

            transcriptPath =
                _transcriptPath;
        }

        if (commands is null)
        {
            return null;
        }

        try
        {
            await CommitAsync(
                cancellationToken);
        }
        finally
        {
            commands.Writer.TryComplete();

            if (workerTask is not null)
            {
                try
                {
                    await workerTask.WaitAsync(
                        cancellationToken);
                }
                finally
                {
                    await CleanupSessionAsync();
                }
            }
            else
            {
                await CleanupSessionAsync();
            }
        }

        return transcriptPath;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _recorder.AudioChunkAvailable -=
            OnAudioChunkAvailable;

        Channel<LocalCommand>? commands;
        Task? workerTask;

        lock (_gate)
        {
            commands =
                _commands;

            workerTask =
                _workerTask;
        }

        commands?.Writer.TryComplete();

        if (workerTask is not null)
        {
            try
            {
                await workerTask;
            }
            catch
            {
            }
        }

        await CleanupSessionAsync();
    }

    private async Task WorkerAsync(
        ChannelReader<LocalCommand> reader,
        WaveFormat sourceFormat,
        WhisperProcessor processor,
        string transcriptPath,
        CancellationToken cancellationToken)
    {
        int targetChunkBytes =
            AlignDown(
                (int)(
                    sourceFormat.AverageBytesPerSecond *
                    _chunkDuration.TotalSeconds),
                sourceFormat.BlockAlign);

        int minimumFlushBytes =
            AlignDown(
                (int)(
                    sourceFormat.AverageBytesPerSecond *
                    MinimumFlushDuration.TotalSeconds),
                sourceFormat.BlockAlign);

        using var pending =
            new MemoryStream(
                targetChunkBytes +
                sourceFormat.AverageBytesPerSecond);

        await foreach (
            LocalCommand command
            in reader.ReadAllAsync(
                cancellationToken))
        {
            switch (command)
            {
                case AudioDataCommand audio:
                    await AppendAudioAsync(
                        pending,
                        audio.Data,
                        targetChunkBytes,
                        sourceFormat,
                        processor,
                        transcriptPath,
                        cancellationToken);

                    break;

                case CommitCommand commit:
                    try
                    {
                        string? text =
                            null;

                        if (pending.Length >=
                            minimumFlushBytes)
                        {
                            byte[] tail =
                                pending.ToArray();

                            pending.SetLength(
                                0);

                            text =
                                await ProcessChunkAsync(
                                    tail,
                                    sourceFormat,
                                    processor,
                                    transcriptPath,
                                    cancellationToken);
                        }
                        else
                        {
                            pending.SetLength(
                                0);
                        }

                        commit.Completion.TrySetResult(
                            text);
                    }
                    catch (Exception ex)
                    {
                        commit.Completion.TrySetException(
                            ex);
                    }

                    break;
            }
        }

        if (pending.Length >=
            minimumFlushBytes)
        {
            await ProcessChunkAsync(
                pending.ToArray(),
                sourceFormat,
                processor,
                transcriptPath,
                CancellationToken.None);
        }
    }

    private async Task AppendAudioAsync(
        MemoryStream pending,
        byte[] data,
        int targetChunkBytes,
        WaveFormat sourceFormat,
        WhisperProcessor processor,
        string transcriptPath,
        CancellationToken cancellationToken)
    {
        int offset =
            0;

        while (offset <
               data.Length)
        {
            int remaining =
                targetChunkBytes -
                (int)pending.Length;

            int take =
                Math.Min(
                    remaining,
                    data.Length -
                    offset);

            pending.Write(
                data,
                offset,
                take);

            offset +=
                take;

            if (pending.Length <
                targetChunkBytes)
            {
                continue;
            }

            byte[] chunk =
                pending.ToArray();

            pending.SetLength(
                0);

            await ProcessChunkAsync(
                chunk,
                sourceFormat,
                processor,
                transcriptPath,
                cancellationToken);
        }
    }

    private static async Task<string?> ProcessChunkAsync(
        byte[] sourceAudio,
        WaveFormat sourceFormat,
        WhisperProcessor processor,
        string transcriptPath,
        CancellationToken cancellationToken)
    {
        float[] monoSamples =
            ResampleToMono16k(
                sourceAudio,
                sourceFormat);

        if (!ContainsSpeech(
            monoSamples))
        {
            return null;
        }

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

        string transcript =
            text
                .ToString()
                .Trim();

        if (transcript.Length == 0)
        {
            return null;
        }

        string line =
            $"[{DateTime.Now:HH:mm:ss}] {transcript}";

        await File.AppendAllTextAsync(
            transcriptPath,
            line +
            Environment.NewLine,
            new UTF8Encoding(
                encoderShouldEmitUTF8Identifier: false),
            cancellationToken);

        Console.WriteLine();
        Console.WriteLine(
            $"LOCAL TEXT -> {transcript}");

        return transcript;
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
                new DownmixToMonoSampleProvider(
                    samples);
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

        var buffer =
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

                if (magnitude >
                    peak)
                {
                    peak =
                        magnitude;
                }

                sumSquares +=
                    sample *
                    sample;
            }

            float rms =
                (float)Math.Sqrt(
                    sumSquares /
                    count);

            if (rms >=
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

            pcm[
                offset +
                1] =
                (byte)(
                    (value >> 8) &
                    0xFF);
        }

        return pcm;
    }

    private static async Task WriteTranscriptHeaderAsync(
        string transcriptPath,
        CancellationToken cancellationToken)
    {
        string? directory =
            Path.GetDirectoryName(
                transcriptPath);

        if (!string.IsNullOrWhiteSpace(
            directory))
        {
            Directory.CreateDirectory(
                directory);
        }

        await File.WriteAllTextAsync(
            transcriptPath,
            "# SliceTranscribe local transcript" +
            Environment.NewLine +
            "# Engine: Whisper.net / whisper.cpp" +
            Environment.NewLine +
            "# Model: multilingual base" +
            Environment.NewLine +
            "# Language: Hungarian (hu)" +
            Environment.NewLine +
            Environment.NewLine,
            new UTF8Encoding(
                encoderShouldEmitUTF8Identifier: false),
            cancellationToken);
    }

    private void OnAudioChunkAvailable(
        object? sender,
        AudioChunkEventArgs e)
    {
        lock (_gate)
        {
            if (_commands is not null)
            {
                _commands.Writer.TryWrite(
                    new AudioDataCommand(
                        e.Data));

                return;
            }

            if (_starting)
            {
                _startupBuffer.Add(
                    e.Data);

                if (_startupBuffer.Count >
                    500)
                {
                    _startupBuffer.RemoveAt(
                        0);
                }
            }
        }
    }

    private Task CleanupSessionAsync()
    {
        WhisperProcessor? processor;
        WhisperFactory? factory;

        lock (_gate)
        {
            processor =
                _processor;

            factory =
                _factory;

            _processor = null;
            _factory = null;
            _commands = null;
            _workerTask = null;
            _sourceFormat = null;
            _starting = false;
            _startupBuffer.Clear();
        }

        processor?.Dispose();
        factory?.Dispose();

        return Task.CompletedTask;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(
                nameof(LocalWhisperTranscriptionController));
        }
    }

    private static int AlignDown(
        int value,
        int alignment)
    {
        if (alignment <= 1)
        {
            return value;
        }

        return value -
               value %
               alignment;
    }

    private abstract record LocalCommand;

    private sealed record AudioDataCommand(
        byte[] Data)
        : LocalCommand;

    private sealed record CommitCommand(
        TaskCompletionSource<string?> Completion)
        : LocalCommand;

    private sealed class DownmixToMonoSampleProvider :
        ISampleProvider
    {
        private readonly ISampleProvider _source;

        private float[] _sourceBuffer =
            Array.Empty<float>();

        public DownmixToMonoSampleProvider(
            ISampleProvider source)
        {
            if (source.WaveFormat.Channels <=
                1)
            {
                throw new ArgumentException(
                    "Source must contain more than one channel.",
                    nameof(source));
            }

            _source =
                source;

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
                float sum =
                    0;

                int sourceOffset =
                    frame *
                    channels;

                for (int channel = 0;
                     channel < channels;
                     channel++)
                {
                    sum +=
                        _sourceBuffer[
                            sourceOffset +
                            channel];
                }

                buffer[
                    offset +
                    frame] =
                    sum /
                    channels;
            }

            return frames;
        }
    }
}
