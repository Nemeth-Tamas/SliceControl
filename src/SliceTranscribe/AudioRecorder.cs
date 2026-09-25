using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace SliceTranscribe;

internal sealed class AudioRecorder :
    IAsyncDisposable
{
    private readonly object _gate = new();

    private readonly MMDevice _device;
    private readonly string _outputDirectory;

    private WasapiCapture? _capture;
    private WaveFileWriter? _writer;

    private TaskCompletionSource<StoppedEventArgs>?
        _stopped;

    private string? _partialPath;
    private string? _finalPath;

    public AudioRecorder(
        MMDevice device,
        string outputDirectory)
    {
        _device = device;
        _outputDirectory = outputDirectory;
    }

    public bool IsRecording { get; private set; }

    public bool IsPaused { get; private set; }

    public event EventHandler<AudioChunkEventArgs>? AudioChunkAvailable;

    public string DeviceName =>
        _device.FriendlyName;

    public string? CurrentFile =>
        _finalPath;

    public WaveFormat? CaptureFormat =>
        _capture?.WaveFormat;

    public string Start()
    {
        lock (_gate)
        {
            if (IsRecording)
            {
                throw new InvalidOperationException(
                    "A recording is already active.");
            }

            Directory.CreateDirectory(
                _outputDirectory);

            string stamp =
                DateTime.Now.ToString(
                    "yyyyMMdd-HHmmss-fff");

            _finalPath =
                Path.Combine(
                    _outputDirectory,
                    $"slice-{stamp}.wav");

            _partialPath =
                Path.Combine(
                    _outputDirectory,
                    $"slice-{stamp}.partial.wav");

            _capture =
                new WasapiCapture(
                    _device);

            _writer =
                new WaveFileWriter(
                    _partialPath,
                    _capture.WaveFormat);

            _stopped =
                new TaskCompletionSource<StoppedEventArgs>(
                    TaskCreationOptions.RunContinuationsAsynchronously);

            _capture.DataAvailable +=
                OnDataAvailable;

            _capture.RecordingStopped +=
                OnRecordingStopped;

            IsPaused = false;
            IsRecording = true;

            try
            {
                PhoneAudioSessionController.RequestMuteAsync(
                    "recording")
                    .GetAwaiter()
                    .GetResult();

                _capture.StartRecording();
            }
            catch
            {
                IsRecording = false;
                IsPaused = false;

                try
                {
                    PhoneAudioSessionController.ReleaseMuteAsync(
                        "recording")
                        .GetAwaiter()
                        .GetResult();
                }
                catch
                {
                }

                _writer.Dispose();
                _capture.Dispose();

                _writer = null;
                _capture = null;
                _stopped = null;

                TryDelete(
                    _partialPath);

                _partialPath = null;
                _finalPath = null;

                throw;
            }

            return _finalPath;
        }
    }

    public bool TogglePause()
    {
        lock (_gate)
        {
            EnsureRecording();

            IsPaused =
                !IsPaused;

            return IsPaused;
        }
    }

    public async Task<string?> StopAsync(
        CancellationToken cancellationToken = default)
    {
        WasapiCapture? capture;
        Task<StoppedEventArgs>? stoppedTask;
        string? partialPath;
        string? finalPath;

        lock (_gate)
        {
            if (!IsRecording)
            {
                return null;
            }

            IsRecording = false;
            IsPaused = false;

            capture = _capture;
            stoppedTask = _stopped?.Task;
            partialPath = _partialPath;
            finalPath = _finalPath;
        }

        capture?.StopRecording();

        StoppedEventArgs? stoppedArgs =
            null;

        if (stoppedTask is not null)
        {
            stoppedArgs =
                await stoppedTask.WaitAsync(
                    TimeSpan.FromSeconds(5),
                    cancellationToken);
        }

        lock (_gate)
        {
            if (_capture is not null)
            {
                _capture.DataAvailable -=
                    OnDataAvailable;

                _capture.RecordingStopped -=
                    OnRecordingStopped;
            }

            _writer?.Flush();
            _writer?.Dispose();
            _capture?.Dispose();

            _writer = null;
            _capture = null;
            _stopped = null;
            _partialPath = null;
        }

        if (stoppedArgs?.Exception is not null)
        {
            throw new InvalidOperationException(
                "Audio capture stopped with an error.",
                stoppedArgs.Exception);
        }

        if (partialPath is not null &&
            finalPath is not null &&
            File.Exists(partialPath))
        {
            File.Move(
                partialPath,
                finalPath,
                overwrite: true);
        }

        await PhoneAudioSessionController.ReleaseMuteAsync(
            "recording",
            cancellationToken);

        return finalPath;
    }

    public async ValueTask DisposeAsync()
    {
        if (IsRecording)
        {
            try
            {
                await StopAsync();
            }
            catch
            {
                // Dispose still releases the endpoint even after a capture
                // failure. The .partial.wav file is intentionally retained.
            }
        }

        _device.Dispose();
    }

    private void OnDataAvailable(
        object? sender,
        WaveInEventArgs e)
    {
        byte[]? transcriptionCopy =
            null;

        lock (_gate)
        {
            if (!IsRecording ||
                IsPaused ||
                _writer is null)
            {
                return;
            }

            _writer.Write(
                e.Buffer,
                0,
                e.BytesRecorded);

            if (AudioChunkAvailable is not null)
            {
                transcriptionCopy =
                    e.Buffer
                        .AsSpan(
                            0,
                            e.BytesRecorded)
                        .ToArray();
            }
        }

        if (transcriptionCopy is not null)
        {
            AudioChunkAvailable?.Invoke(
                this,
                new AudioChunkEventArgs(
                    transcriptionCopy));
        }
    }

    private void OnRecordingStopped(
        object? sender,
        StoppedEventArgs e)
    {
        _stopped?.TrySetResult(e);
    }

    private void EnsureRecording()
    {
        if (!IsRecording)
        {
            throw new InvalidOperationException(
                "No recording is active.");
        }
    }

    private static void TryDelete(
        string? path)
    {
        if (path is null)
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // A failed startup should not hide the original capture error.
        }
    }
}
