using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace SliceTranscribe;

internal sealed class CallRecorder :
    IAsyncDisposable
{
    private readonly MMDevice _renderDevice;
    private readonly MMDevice _microphoneDevice;
    private readonly string _outputDirectory;

    private WasapiLoopbackCapture? _remoteCapture;
    private WasapiCapture? _localCapture;

    private WaveFileWriter? _remoteWriter;
    private WaveFileWriter? _localWriter;

    private TaskCompletionSource<StoppedEventArgs>? _remoteStopped;
    private TaskCompletionSource<StoppedEventArgs>? _localStopped;

    public CallRecorder(
        MMDevice renderDevice,
        MMDevice microphoneDevice,
        string outputDirectory)
    {
        _renderDevice =
            renderDevice;

        _microphoneDevice =
            microphoneDevice;

        _outputDirectory =
            outputDirectory;
    }

    public bool IsRecording { get; private set; }

    public string? RemotePath { get; private set; }

    public string? LocalPath { get; private set; }

    public string RenderDeviceName =>
        _renderDevice.FriendlyName;

    public string MicrophoneDeviceName =>
        _microphoneDevice.FriendlyName;

    public void Start()
    {
        if (IsRecording)
        {
            throw new InvalidOperationException(
                "Call recording is already active.");
        }

        Directory.CreateDirectory(
            _outputDirectory);

        string stamp =
            DateTime.Now.ToString(
                "yyyyMMdd-HHmmss-fff");

        RemotePath =
            Path.Combine(
                _outputDirectory,
                $"call-{stamp}.remote.wav");

        LocalPath =
            Path.Combine(
                _outputDirectory,
                $"call-{stamp}.local.wav");

        _remoteCapture =
            new WasapiLoopbackCapture(
                _renderDevice);

        _localCapture =
            new WasapiCapture(
                _microphoneDevice);

        _remoteWriter =
            new WaveFileWriter(
                RemotePath,
                _remoteCapture.WaveFormat);

        _localWriter =
            new WaveFileWriter(
                LocalPath,
                _localCapture.WaveFormat);

        _remoteStopped =
            new TaskCompletionSource<StoppedEventArgs>(
                TaskCreationOptions.RunContinuationsAsynchronously);

        _localStopped =
            new TaskCompletionSource<StoppedEventArgs>(
                TaskCreationOptions.RunContinuationsAsynchronously);

        _remoteCapture.DataAvailable +=
            OnRemoteDataAvailable;

        _localCapture.DataAvailable +=
            OnLocalDataAvailable;

        _remoteCapture.RecordingStopped +=
            (_, args) =>
                _remoteStopped.TrySetResult(
                    args);

        _localCapture.RecordingStopped +=
            (_, args) =>
                _localStopped.TrySetResult(
                    args);

        IsRecording =
            true;

        try
        {
            _remoteCapture.StartRecording();

            _localCapture.StartRecording();
        }
        catch
        {
            IsRecording =
                false;

            DisposeCaptureObjects();

            throw;
        }
    }

    public async Task StopAsync(
        CancellationToken cancellationToken = default)
    {
        if (!IsRecording)
        {
            return;
        }

        IsRecording =
            false;

        _remoteCapture?.StopRecording();

        _localCapture?.StopRecording();

        if (_remoteStopped is not null)
        {
            await _remoteStopped.Task.WaitAsync(
                TimeSpan.FromSeconds(5),
                cancellationToken);
        }

        if (_localStopped is not null)
        {
            await _localStopped.Task.WaitAsync(
                TimeSpan.FromSeconds(5),
                cancellationToken);
        }

        DisposeCaptureObjects();
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

        DisposeCaptureObjects();

        _renderDevice.Dispose();

        _microphoneDevice.Dispose();
    }

    private void OnRemoteDataAvailable(
        object? sender,
        WaveInEventArgs e)
    {
        if (!IsRecording ||
            _remoteWriter is null)
        {
            return;
        }

        _remoteWriter.Write(
            e.Buffer,
            0,
            e.BytesRecorded);
    }

    private void OnLocalDataAvailable(
        object? sender,
        WaveInEventArgs e)
    {
        if (!IsRecording ||
            _localWriter is null)
        {
            return;
        }

        _localWriter.Write(
            e.Buffer,
            0,
            e.BytesRecorded);
    }

    private void DisposeCaptureObjects()
    {
        if (_remoteCapture is not null)
        {
            _remoteCapture.DataAvailable -=
                OnRemoteDataAvailable;
        }

        if (_localCapture is not null)
        {
            _localCapture.DataAvailable -=
                OnLocalDataAvailable;
        }

        _remoteWriter?.Flush();
        _localWriter?.Flush();

        _remoteWriter?.Dispose();
        _localWriter?.Dispose();

        _remoteCapture?.Dispose();
        _localCapture?.Dispose();

        _remoteWriter =
            null;

        _localWriter =
            null;

        _remoteCapture =
            null;

        _localCapture =
            null;

        _remoteStopped =
            null;

        _localStopped =
            null;
    }
}
