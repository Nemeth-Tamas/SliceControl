using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace SliceTranscribe;

internal sealed class AnnouncementRecorder :
    IAsyncDisposable
{
    private readonly object _gate =
        new();

    private WasapiCapture? _capture;
    private WaveFileWriter? _writer;
    private TaskCompletionSource<StoppedEventArgs>? _stopped;
    private MMDevice? _device;
    private string? _partialPath;
    private string? _finalPath;

    public bool IsRecording { get; private set; }

    public string? CurrentFile =>
        _finalPath;

    public string Start(
        string outputDirectory,
        string name,
        string? microphoneName)
    {
        lock (_gate)
        {
            if (IsRecording)
            {
                throw new InvalidOperationException(
                    "An announcement recording is already active.");
            }

            Directory.CreateDirectory(
                outputDirectory);

            string safeName =
                SanitizeName(
                    name);

            _finalPath =
                Path.Combine(
                    outputDirectory,
                    safeName + ".wav");

            _partialPath =
                Path.Combine(
                    outputDirectory,
                    safeName + ".partial.wav");

            _device =
                MicrophoneSelector.Resolve(
                    microphoneName);

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

            IsRecording =
                true;

            try
            {
                _capture.StartRecording();
            }
            catch
            {
                IsRecording =
                    false;

                CleanupCapture();

                TryDelete(
                    _partialPath);

                _partialPath =
                    null;

                _finalPath =
                    null;

                throw;
            }

            return _finalPath;
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

            IsRecording =
                false;

            capture =
                _capture;

            stoppedTask =
                _stopped?.Task;

            partialPath =
                _partialPath;

            finalPath =
                _finalPath;
        }

        capture?.StopRecording();

        StoppedEventArgs? stoppedArgs =
            null;

        if (stoppedTask is not null)
        {
            stoppedArgs =
                await stoppedTask.WaitAsync(
                    TimeSpan.FromSeconds(
                        5),
                    cancellationToken);
        }

        lock (_gate)
        {
            CleanupCapture();

            _partialPath =
                null;
        }

        if (stoppedArgs?.Exception is not null)
        {
            throw new InvalidOperationException(
                "Announcement recording stopped with an error.",
                stoppedArgs.Exception);
        }

        if (
            partialPath is not null &&
            finalPath is not null &&
            File.Exists(
                partialPath))
        {
            File.Move(
                partialPath,
                finalPath,
                overwrite: true);
        }

        return finalPath;
    }

    private void OnDataAvailable(
        object? sender,
        WaveInEventArgs e)
    {
        lock (_gate)
        {
            if (!IsRecording ||
                _writer is null)
            {
                return;
            }

            _writer.Write(
                e.Buffer,
                0,
                e.BytesRecorded);
        }
    }

    private void OnRecordingStopped(
        object? sender,
        StoppedEventArgs e)
    {
        _stopped?.TrySetResult(
            e);
    }

    private void CleanupCapture()
    {
        if (_capture is not null)
        {
            _capture.DataAvailable -=
                OnDataAvailable;

            _capture.RecordingStopped -=
                OnRecordingStopped;
        }

        try
        {
            _writer?.Flush();
        }
        catch
        {
        }

        _writer?.Dispose();
        _capture?.Dispose();
        _device?.Dispose();

        _writer =
            null;

        _capture =
            null;

        _device =
            null;

        _stopped =
            null;
    }

    private static string SanitizeName(
        string name)
    {
        string trimmed =
            name.Trim();

        if (string.IsNullOrWhiteSpace(
            trimmed))
        {
            trimmed =
                "announcement-" +
                DateTime.Now.ToString(
                    "yyyyMMdd-HHmmss");
        }

        foreach (char invalid in
                 Path.GetInvalidFileNameChars())
        {
            trimmed =
                trimmed.Replace(
                    invalid,
                    '-');
        }

        trimmed =
            trimmed.Trim(
                ' ',
                '.');

        if (string.IsNullOrWhiteSpace(
            trimmed))
        {
            trimmed =
                "announcement-" +
                DateTime.Now.ToString(
                    "yyyyMMdd-HHmmss");
        }

        return trimmed;
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
            }
        }

        lock (_gate)
        {
            CleanupCapture();
        }
    }
}
