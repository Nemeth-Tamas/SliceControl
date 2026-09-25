using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System.Buffers.Binary;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace SliceTranscribe;

internal sealed class OpenAIRealtimeTranscriber :
    IAsyncDisposable
{
    private const string Model =
        "gpt-live-transcribe";

    private const int TargetSampleRate =
        24000;

    private const int TargetChunkSamples =
        2400;

    private const int MinimumCommitBytes =
        TargetSampleRate * 2 / 10;

    private static readonly string[] Keywords =
    {
        "Cellnet",
        "Dunaújváros",
        "iPhone",
        "Android",
        "Windows",
        "Samsung",
        "Xiaomi",
        "USB",
        "SSD",
        "RAM",
        "Wi-Fi"
    };

    private readonly string _apiKey;
    private readonly string _transcriptPath;

    private readonly ClientWebSocket _socket =
        new();

    private readonly CancellationTokenSource _lifetime =
        new();

    private readonly Channel<AudioCommand> _commands =
        Channel.CreateUnbounded<AudioCommand>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false
            });

    private readonly Channel<string> _completedTranscripts =
        Channel.CreateUnbounded<string>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = true
            });

    private readonly TaskCompletionSource<bool> _sessionReady =
        new(
            TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly BufferedWaveProvider _bufferedInput;
    private readonly WdlResamplingSampleProvider _resampler;
    private readonly int _normalDrainThresholdBytes;

    private Task? _receiveTask;
    private Task? _audioPumpTask;

    private bool _started;
    private bool _closed;

    public OpenAIRealtimeTranscriber(
        string apiKey,
        WaveFormat sourceFormat,
        string transcriptPath)
    {
        _apiKey =
            string.IsNullOrWhiteSpace(apiKey)
                ? throw new ArgumentException(
                    "OpenAI API key is required.",
                    nameof(apiKey))
                : apiKey;

        _transcriptPath =
            transcriptPath;

        WaveFormat sampleFormat =
            sourceFormat is WaveFormatExtensible extensible
                ? extensible.ToStandardWaveFormat()
                : sourceFormat;

        _bufferedInput =
            new BufferedWaveProvider(
                sampleFormat)
            {
                BufferDuration =
                    TimeSpan.FromSeconds(10),
                DiscardOnBufferOverflow = false,
                ReadFully = false
            };

        _normalDrainThresholdBytes =
            Math.Max(
                sampleFormat.BlockAlign,
                AlignDown(
                    sampleFormat.AverageBytesPerSecond / 8,
                    sampleFormat.BlockAlign));

        ISampleProvider samples =
            _bufferedInput
                .ToSampleProvider();

        if (samples.WaveFormat.Channels > 1)
        {
            samples =
                new DownmixToMonoSampleProvider(
                    samples);
        }

        _resampler =
            new WdlResamplingSampleProvider(
                samples,
                TargetSampleRate);
    }

    public event Action<string>? PartialTranscript;

    public event Action<string>? FinalTranscript;

    public event Action<string>? Error;

    public string TranscriptPath =>
        _transcriptPath;

    public async Task StartAsync(
        CancellationToken cancellationToken = default)
    {
        if (_started)
        {
            throw new InvalidOperationException(
                "The transcription session is already started.");
        }

        _started = true;

        string? directory =
            Path.GetDirectoryName(
                _transcriptPath);

        if (!string.IsNullOrWhiteSpace(
            directory))
        {
            Directory.CreateDirectory(
                directory);
        }

        await File.WriteAllTextAsync(
            _transcriptPath,
            "# SliceTranscribe live transcript" +
            Environment.NewLine +
            $"# Model: {Model}" +
            Environment.NewLine +
            "# Language: Hungarian (hu)" +
            Environment.NewLine +
            Environment.NewLine,
            new UTF8Encoding(
                encoderShouldEmitUTF8Identifier: false),
            cancellationToken);

        _socket.Options.SetRequestHeader(
            "Authorization",
            $"Bearer {_apiKey}");

        var uri =
            new Uri(
                "wss://api.openai.com/v1/realtime" +
                $"?model={Uri.EscapeDataString(Model)}");

        await _socket.ConnectAsync(
            uri,
            cancellationToken);

        _receiveTask =
            ReceiveLoopAsync(
                _lifetime.Token);

        await SendJsonAsync(
            new
            {
                type = "session.update",
                session = new
                {
                    type = "transcription",
                    audio = new
                    {
                        input = new
                        {
                            format = new
                            {
                                type = "audio/pcm",
                                rate = TargetSampleRate
                            },
                            transcription = new
                            {
                                model = Model,
                                prompt =
                                    "Hungarian conversation in an electronics repair and IT shop. " +
                                    "Preserve names, model numbers, prices, phone numbers, product names, " +
                                    "technical terms, abbreviations, and Hungarian accents accurately.",
                                keywords = Keywords,
                                languages =
                                    new[] { "hu" },
                                delay = "low"
                            },
                            turn_detection =
                                (object?)null
                        }
                    }
                }
            },
            cancellationToken);

        await _sessionReady.Task.WaitAsync(
            TimeSpan.FromSeconds(15),
            cancellationToken);

        _audioPumpTask =
            AudioPumpAsync(
                _lifetime.Token);
    }

    public void EnqueueAudio(
        byte[] sourceAudio)
    {
        if (!_started ||
            _closed ||
            sourceAudio.Length == 0)
        {
            return;
        }

        _commands.Writer.TryWrite(
            new AudioDataCommand(
                sourceAudio));
    }

    public async Task<string?> CommitAsync(
        CancellationToken cancellationToken = default)
    {
        if (!_started ||
            _closed)
        {
            return null;
        }

        var completion =
            new TaskCompletionSource<string?>(
                TaskCreationOptions.RunContinuationsAsynchronously);

        await _commands.Writer.WriteAsync(
            new CommitCommand(
                completion,
                cancellationToken),
            cancellationToken);

        return await completion.Task.WaitAsync(
            TimeSpan.FromSeconds(30),
            cancellationToken);
    }

    public async Task CloseAsync()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;

        _commands.Writer.TryComplete();

        if (_audioPumpTask is not null)
        {
            try
            {
                await _audioPumpTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        if (_socket.State is WebSocketState.Open)
        {
            try
            {
                await _socket.CloseOutputAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "SliceTranscribe finished",
                    CancellationToken.None);
            }
            catch
            {
            }
        }

        if (_receiveTask is not null)
        {
            try
            {
                await _receiveTask.WaitAsync(
                    TimeSpan.FromSeconds(5));
            }
            catch
            {
                _lifetime.Cancel();
            }
        }

        _lifetime.Cancel();
        _socket.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync();
        _lifetime.Dispose();
    }

    private async Task AudioPumpAsync(
        CancellationToken cancellationToken)
    {
        var sampleBuffer =
            new float[TargetChunkSamples];

        int bytesSinceCommit =
            0;

        await foreach (
            AudioCommand command
            in _commands.Reader.ReadAllAsync(
                cancellationToken))
        {
            switch (command)
            {
                case AudioDataCommand audio:
                    _bufferedInput.AddSamples(
                        audio.Data,
                        0,
                        audio.Data.Length);

                    bytesSinceCommit +=
                        await DrainResamplerAsync(
                            sampleBuffer,
                            flush: false,
                            cancellationToken);

                    break;

                case CommitCommand commit:
                    try
                    {
                        bytesSinceCommit +=
                            await DrainResamplerAsync(
                                sampleBuffer,
                                flush: true,
                                cancellationToken);

                        if (bytesSinceCommit <
                            MinimumCommitBytes)
                        {
                            commit.Completion.TrySetResult(
                                null);

                            break;
                        }

                        await SendJsonAsync(
                            new
                            {
                                type =
                                    "input_audio_buffer.commit"
                            },
                            cancellationToken);

                        string transcript =
                            await _completedTranscripts
                                .Reader
                                .ReadAsync(
                                    cancellationToken)
                                .AsTask()
                                .WaitAsync(
                                    TimeSpan.FromSeconds(30),
                                    commit.CancellationToken);

                        bytesSinceCommit = 0;

                        commit.Completion.TrySetResult(
                            transcript);
                    }
                    catch (Exception ex)
                    {
                        commit.Completion.TrySetException(
                            ex);
                    }

                    break;
            }
        }
    }

    private async Task<int> DrainResamplerAsync(
        float[] sampleBuffer,
        bool flush,
        CancellationToken cancellationToken)
    {
        int totalBytes =
            0;

        // NAudio 2.x's WDL provider behaves best when its streaming source has
        // enough data available to satisfy a read. Keep about 125 ms buffered
        // during normal streaming, then allow the remainder through on commit.
        for (int pass = 0;
             pass < 128;
             pass++)
        {
            if (!flush &&
                _bufferedInput.BufferedBytes <
                    _normalDrainThresholdBytes)
            {
                break;
            }

            if (flush &&
                _bufferedInput.BufferedBytes == 0 &&
                pass == 0)
            {
                break;
            }

            int samplesRead =
                _resampler.Read(
                    sampleBuffer,
                    0,
                    sampleBuffer.Length);

            if (samplesRead <= 0)
            {
                break;
            }

            byte[] pcm16 =
                new byte[samplesRead * 2];

            for (int i = 0;
                 i < samplesRead;
                 i++)
            {
                float sample =
                    Math.Clamp(
                        sampleBuffer[i],
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

                BinaryPrimitives
                    .WriteInt16LittleEndian(
                        pcm16.AsSpan(
                            i * 2,
                            2),
                        value);
            }

            await SendJsonAsync(
                new
                {
                    type =
                        "input_audio_buffer.append",
                    audio =
                        Convert.ToBase64String(
                            pcm16)
                },
                cancellationToken);

            totalBytes +=
                pcm16.Length;

            if (samplesRead <
                sampleBuffer.Length)
            {
                break;
            }
        }

        return totalBytes;
    }

    private async Task ReceiveLoopAsync(
        CancellationToken cancellationToken)
    {
        byte[] buffer =
            new byte[32 * 1024];

        try
        {
            while (!cancellationToken.IsCancellationRequested &&
                   _socket.State is
                       WebSocketState.Open or
                       WebSocketState.CloseSent)
            {
                using var message =
                    new MemoryStream();

                WebSocketReceiveResult result;

                do
                {
                    result =
                        await _socket.ReceiveAsync(
                            new ArraySegment<byte>(
                                buffer),
                            cancellationToken);

                    if (result.MessageType ==
                        WebSocketMessageType.Close)
                    {
                        _completedTranscripts
                            .Writer
                            .TryComplete();

                        return;
                    }

                    message.Write(
                        buffer,
                        0,
                        result.Count);
                }
                while (!result.EndOfMessage);

                if (result.MessageType !=
                    WebSocketMessageType.Text)
                {
                    continue;
                }

                string json =
                    Encoding.UTF8.GetString(
                        message.ToArray());

                HandleServerEvent(
                    json);
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _sessionReady.TrySetException(
                ex);

            _completedTranscripts
                .Writer
                .TryComplete(
                    ex);

            Error?.Invoke(
                ex.Message);
        }
        finally
        {
            _completedTranscripts
                .Writer
                .TryComplete();
        }
    }

    private void HandleServerEvent(
        string json)
    {
        using JsonDocument document =
            JsonDocument.Parse(
                json);

        JsonElement root =
            document.RootElement;

        if (!root.TryGetProperty(
                "type",
                out JsonElement typeElement))
        {
            return;
        }

        string? type =
            typeElement.GetString();

        switch (type)
        {
            case "session.updated":
                _sessionReady.TrySetResult(
                    true);
                break;

            case "conversation.item.input_audio_transcription.delta":
                if (root.TryGetProperty(
                        "delta",
                        out JsonElement deltaElement))
                {
                    string? delta =
                        deltaElement.GetString();

                    if (!string.IsNullOrEmpty(
                        delta))
                    {
                        PartialTranscript?.Invoke(
                            delta);
                    }
                }

                break;

            case "conversation.item.input_audio_transcription.completed":
                string transcript =
                    root.TryGetProperty(
                        "transcript",
                        out JsonElement transcriptElement)
                        ? transcriptElement.GetString()
                            ?? string.Empty
                        : string.Empty;

                AppendFinalTranscript(
                    transcript);

                FinalTranscript?.Invoke(
                    transcript);

                _completedTranscripts
                    .Writer
                    .TryWrite(
                        transcript);

                break;

            case "error":
                string message =
                    ReadErrorMessage(
                        root);

                var exception =
                    new InvalidOperationException(
                        $"OpenAI realtime transcription error: {message}");

                _sessionReady.TrySetException(
                    exception);

                _completedTranscripts
                    .Writer
                    .TryComplete(
                        exception);

                Error?.Invoke(
                    message);

                break;
        }
    }

    private void AppendFinalTranscript(
        string transcript)
    {
        if (string.IsNullOrWhiteSpace(
            transcript))
        {
            return;
        }

        File.AppendAllText(
            _transcriptPath,
            $"[{DateTime.Now:HH:mm:ss}] {transcript.Trim()}" +
            Environment.NewLine,
            new UTF8Encoding(
                encoderShouldEmitUTF8Identifier: false));
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

    private static string ReadErrorMessage(
        JsonElement root)
    {
        if (root.TryGetProperty(
                "error",
                out JsonElement error))
        {
            if (error.ValueKind ==
                    JsonValueKind.Object &&
                error.TryGetProperty(
                    "message",
                    out JsonElement message))
            {
                return message.GetString()
                    ?? "Unknown API error.";
            }

            return error.ToString();
        }

        return "Unknown API error.";
    }

    private async Task SendJsonAsync(
        object payload,
        CancellationToken cancellationToken)
    {
        string json =
            JsonSerializer.Serialize(
                payload);

        byte[] bytes =
            Encoding.UTF8.GetBytes(
                json);

        await _socket.SendAsync(
            new ArraySegment<byte>(
                bytes),
            WebSocketMessageType.Text,
            endOfMessage: true,
            cancellationToken);
    }

    private abstract record AudioCommand;

    private sealed record AudioDataCommand(
        byte[] Data)
        : AudioCommand;

    private sealed record CommitCommand(
        TaskCompletionSource<string?> Completion,
        CancellationToken CancellationToken)
        : AudioCommand;

    private sealed class DownmixToMonoSampleProvider :
        ISampleProvider
    {
        private readonly ISampleProvider _source;
        private float[] _sourceBuffer =
            Array.Empty<float>();

        public DownmixToMonoSampleProvider(
            ISampleProvider source)
        {
            if (source.WaveFormat.Channels <= 1)
            {
                throw new ArgumentException(
                    "Source must contain more than one channel.",
                    nameof(source));
            }

            _source = source;

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
                count * channels;

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
