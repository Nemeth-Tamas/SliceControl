using NAudio.Wave;

namespace SliceTranscribe;

internal sealed class LiveTranscriptionController :
    IAsyncDisposable
{
    private readonly object _gate =
        new();

    private readonly AudioRecorder _recorder;
    private readonly string? _apiKey;

    private readonly List<byte[]> _startupBuffer =
        new();

    private OpenAIRealtimeTranscriber? _session;

    private bool _starting;
    private bool _disposed;

    public LiveTranscriptionController(
        AudioRecorder recorder,
        string? apiKey)
    {
        _recorder = recorder;
        _apiKey = apiKey;

        _recorder.AudioChunkAvailable +=
            OnAudioChunkAvailable;
    }

    public bool Enabled =>
        !string.IsNullOrWhiteSpace(
            _apiKey);

    public string? TranscriptPath =>
        _session?.TranscriptPath;

    public async Task<string?> StartAsync(
        string wavPath,
        CancellationToken cancellationToken = default)
    {
        if (!Enabled)
        {
            return null;
        }

        WaveFormat format =
            _recorder.CaptureFormat
            ?? throw new InvalidOperationException(
                "Microphone capture format is not available.");

        lock (_gate)
        {
            if (_session is not null ||
                _starting)
            {
                throw new InvalidOperationException(
                    "A live transcription session is already active.");
            }

            _starting = true;
            _startupBuffer.Clear();
        }

        string transcriptPath =
            Path.ChangeExtension(
                wavPath,
                ".txt");

        var session =
            new OpenAIRealtimeTranscriber(
                _apiKey!,
                format,
                transcriptPath);

        session.PartialTranscript +=
            delta =>
                Console.Write(
                    delta);

        session.FinalTranscript +=
            transcript =>
            {
                Console.WriteLine();

                if (!string.IsNullOrWhiteSpace(
                    transcript))
                {
                    Console.WriteLine(
                        $"TEXT -> {transcript.Trim()}");
                }
            };

        session.Error +=
            message =>
                Console.Error.WriteLine(
                    $"TRANSCRIPTION ERROR -> {message}");

        try
        {
            await session.StartAsync(
                cancellationToken);

            lock (_gate)
            {
                _session =
                    session;

                foreach (
                    byte[] chunk
                    in _startupBuffer)
                {
                    session.EnqueueAudio(
                        chunk);
                }

                _startupBuffer.Clear();
                _starting = false;
            }

            return transcriptPath;
        }
        catch
        {
            lock (_gate)
            {
                _starting = false;
                _startupBuffer.Clear();
            }

            await session.DisposeAsync();
            throw;
        }
    }

    public async Task<string?> CommitAsync(
        CancellationToken cancellationToken = default)
    {
        OpenAIRealtimeTranscriber? session;

        lock (_gate)
        {
            session =
                _session;
        }

        if (session is null)
        {
            return null;
        }

        return await session.CommitAsync(
            cancellationToken);
    }

    public async Task<string?> FinishAsync(
        CancellationToken cancellationToken = default)
    {
        OpenAIRealtimeTranscriber? session;

        lock (_gate)
        {
            session =
                _session;

            _session = null;
            _startupBuffer.Clear();
            _starting = false;
        }

        if (session is null)
        {
            return null;
        }

        string transcriptPath =
            session.TranscriptPath;

        try
        {
            await session.CommitAsync(
                cancellationToken);
        }
        finally
        {
            await session.DisposeAsync();
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

        OpenAIRealtimeTranscriber? session;

        lock (_gate)
        {
            session =
                _session;

            _session = null;
            _startupBuffer.Clear();
            _starting = false;
        }

        if (session is not null)
        {
            try
            {
                await session.CommitAsync();
            }
            catch
            {
            }

            await session.DisposeAsync();
        }
    }

    private void OnAudioChunkAvailable(
        object? sender,
        AudioChunkEventArgs e)
    {
        lock (_gate)
        {
            if (_session is not null)
            {
                _session.EnqueueAudio(
                    e.Data);

                return;
            }

            if (_starting)
            {
                _startupBuffer.Add(
                    e.Data);

                // Keep startup buffering bounded to roughly several seconds,
                // even if the API connection stalls.
                if (_startupBuffer.Count >
                    100)
                {
                    _startupBuffer.RemoveAt(
                        0);
                }
            }
        }
    }
}
