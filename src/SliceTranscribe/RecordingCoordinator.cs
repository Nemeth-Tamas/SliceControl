using SliceControl;

namespace SliceTranscribe;

internal sealed class RecordingCoordinator
{
    private readonly SliceDevice _slice;
    private readonly AudioRecorder _recorder;
    private readonly ITranscriptionController _transcription;
    private readonly SemaphoreSlim _gate =
        new(
            1,
            1);

    public RecordingCoordinator(
        SliceDevice slice,
        AudioRecorder recorder,
        ITranscriptionController transcription)
    {
        _slice =
            slice;

        _recorder =
            recorder;

        _transcription =
            transcription;
    }

    public bool IsRecording =>
        _recorder.IsRecording;

    public bool IsPaused =>
        _recorder.IsPaused;

    public string? CurrentFile =>
        _recorder.CurrentFile;

    public async Task<string?> StartAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(
            cancellationToken);

        try
        {
            if (_recorder.IsRecording)
            {
                return _recorder.CurrentFile;
            }

            bool radioHeld =
                await RadioController.RequestPauseAsync(
                    "recording",
                    cancellationToken);

            if (radioHeld)
            {
                await Task.Delay(
                    100,
                    cancellationToken);
            }

            string path;

            try
            {
                path =
                    _recorder.Start();
            }
            catch
            {
                await RadioController.ReleasePauseAsync(
                    "recording",
                    CancellationToken.None);

                throw;
            }

            _slice.Lights.EnterCallAnimation();

            await Task.Delay(
                120,
                cancellationToken);

            _slice.Lights.ShowActiveCall();

            Console.WriteLine(
                $"RECORDING -> {path}");

            Console.WriteLine(
                $"CAPTURE -> {_recorder.CaptureFormat}");

            if (_transcription.Enabled)
            {
                try
                {
                    string? transcriptPath =
                        await _transcription.StartAsync(
                            path,
                            cancellationToken);

                    Console.WriteLine(
                        $"TRANSCRIBING -> {transcriptPath}");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine(
                        $"Could not start transcription: {ex.Message}");

                    Console.Error.WriteLine(
                        "Recording will continue without transcription.");
                }
            }

            return path;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string?> StopAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(
            cancellationToken);

        try
        {
            if (!_recorder.IsRecording)
            {
                return null;
            }

            string? saved =
                await _recorder.StopAsync(
                    cancellationToken);

            _slice.Lights.ExitAnimation();

            await RadioController.ReleasePauseAsync(
                "recording",
                cancellationToken);

            string? transcript =
                null;

            try
            {
                transcript =
                    await _transcription.FinishAsync(
                        cancellationToken);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"Could not finalize transcription: {ex.Message}");
            }

            Console.WriteLine();
            Console.WriteLine(
                $"SAVED -> {saved}");

            if (transcript is not null)
            {
                Console.WriteLine(
                    $"TRANSCRIPT -> {transcript}");
            }

            return saved;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool?> TogglePauseAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(
            cancellationToken);

        try
        {
            if (!_recorder.IsRecording)
            {
                return null;
            }

            bool paused =
                _recorder.TogglePause();

            if (paused)
            {
                _slice.Lights.ShowActiveMutedCall();

                Console.WriteLine(
                    "PAUSED");

                if (_transcription.Enabled)
                {
                    _ =
                        CommitTranscriptSegmentAsync();
                }
            }
            else
            {
                _slice.Lights.ShowActiveCall();

                Console.WriteLine(
                    "RECORDING");
            }

            return paused;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopForShutdownAsync()
    {
        try
        {
            if (_recorder.IsRecording)
            {
                await StopAsync(
                    CancellationToken.None);
            }
            else
            {
                try
                {
                    string? transcript =
                        await _transcription.FinishAsync();

                    if (transcript is not null)
                    {
                        Console.WriteLine(
                            $"Transcript: {transcript}");
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine(
                        $"Could not finalize transcription: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"Could not finalize recording: {ex.Message}");
        }
    }

    private async Task CommitTranscriptSegmentAsync()
    {
        try
        {
            await _transcription.CommitAsync();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"Could not finalize transcript segment: {ex.Message}");
        }
    }
}
