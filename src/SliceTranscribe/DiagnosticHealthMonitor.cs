using System.Diagnostics;
using NAudio.CoreAudioApi;

namespace SliceTranscribe;

internal sealed class DiagnosticHealthMonitor
{
    private static readonly TimeSpan Interval =
        TimeSpan.FromSeconds(
            15);

    private readonly AssistantMode _assistant;
    private readonly RecordingCoordinator _recording;

    public DiagnosticHealthMonitor(
        AssistantMode assistant,
        RecordingCoordinator recording)
    {
        _assistant =
            assistant;

        _recording =
            recording;
    }

    public async Task RunAsync(
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                WriteSnapshot();
            }
            catch (Exception ex)
            {
                DiagnosticLog.Error(
                    "health",
                    "snapshot_failed",
                    ex);
            }

            try
            {
                await Task.Delay(
                    Interval,
                    cancellationToken);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private void WriteSnapshot()
    {
        using Process process =
            Process.GetCurrentProcess();

        AssistantStatus assistant =
            _assistant.GetStatus();

        string? outputName =
            null;

        string? captureName =
            null;

        int? masterVolume =
            null;

        bool? masterMuted =
            null;

        float? masterPeak =
            null;

        var sessions =
            new List<object>();

        try
        {
            using var enumerator =
                new MMDeviceEnumerator();

            using MMDevice output =
                enumerator.GetDefaultAudioEndpoint(
                    DataFlow.Render,
                    Role.Multimedia);

            outputName =
                output.FriendlyName;

            masterVolume =
                (int)Math.Round(
                    output.AudioEndpointVolume.MasterVolumeLevelScalar *
                    100.0f);

            masterMuted =
                output.AudioEndpointVolume.Mute;

            masterPeak =
                output.AudioMeterInformation.MasterPeakValue;

            AudioSessionManager manager =
                output.AudioSessionManager;

            manager.RefreshSessions();

            SessionCollection collection =
                manager.Sessions;

            for (int i = 0;
                 i < collection.Count;
                 i++)
            {
                try
                {
                    using AudioSessionControl session =
                        collection[i];

                    uint processId =
                        session.GetProcessID;

                    float peak =
                        session.AudioMeterInformation.MasterPeakValue;

                    bool muted =
                        session.SimpleAudioVolume.Mute;

                    float volume =
                        session.SimpleAudioVolume.Volume;

                    string processName =
                        ResolveProcessName(
                            processId);

                    bool interesting =
                        peak >=
                            0.0005f ||
                        muted ||
                        processName.Equals(
                            "SonoBus",
                            StringComparison.OrdinalIgnoreCase) ||
                        processName.Equals(
                            "vlc",
                            StringComparison.OrdinalIgnoreCase) ||
                        processName.Equals(
                            "SliceTranscribe",
                            StringComparison.OrdinalIgnoreCase);

                    if (!interesting)
                    {
                        continue;
                    }

                    sessions.Add(
                        new
                        {
                            pid =
                                processId,
                            process =
                                processName,
                            display_name =
                                session.DisplayName,
                            state =
                                session.State.ToString(),
                            peak =
                                Math.Round(
                                    peak,
                                    6),
                            muted,
                            volume =
                                Math.Round(
                                    volume,
                                    4)
                        });
                }
                catch
                {
                    // Audio sessions can disappear while enumerating them.
                }
            }

            using MMDevice capture =
                enumerator.GetDefaultAudioEndpoint(
                    DataFlow.Capture,
                    Role.Multimedia);

            captureName =
                capture.FriendlyName;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Error(
                "health",
                "audio_snapshot_failed",
                ex);
        }

        DiagnosticLog.Event(
            "health",
            "snapshot",
            new
            {
                working_set_mb =
                    Math.Round(
                        process.WorkingSet64 /
                        1024d /
                        1024d,
                        1),
                private_mb =
                    Math.Round(
                        process.PrivateMemorySize64 /
                        1024d /
                        1024d,
                        1),
                handles =
                    SafeHandleCount(
                        process),
                threads =
                    process.Threads.Count,
                recording_active =
                    _recording.IsRecording,
                recording_paused =
                    _recording.IsPaused,
                assistant =
                    new
                    {
                        assistant.Enabled,
                        assistant.State,
                        assistant.MicrophoneName,
                        assistant.CaptureFormat,
                        assistant.CaptureAgeMs,
                        assistant.CaptureCallbackCount,
                        assistant.CaptureBytes,
                        assistant.LastCapturePeak,
                        assistant.CaptureRestartCount,
                        assistant.LastWakeLatencyMs,
                        assistant.LastRemoteCommandLatencyMs,
                        assistant.LastLocalCommandLatencyMs,
                        assistant.LastError
                    },
                audio =
                    new
                    {
                        output =
                            outputName,
                        capture =
                            captureName,
                        master_volume =
                            masterVolume,
                        master_muted =
                            masterMuted,
                        master_peak =
                            masterPeak,
                        sessions
                    }
            });
    }

    private static string ResolveProcessName(
        uint processId)
    {
        if (processId == 0)
        {
            return "system";
        }

        try
        {
            using Process process =
                Process.GetProcessById(
                    checked(
                        (int)processId));

            return process.ProcessName;
        }
        catch
        {
            return "unknown";
        }
    }

    private static int? SafeHandleCount(
        Process process)
    {
        try
        {
            return process.HandleCount;
        }
        catch
        {
            return null;
        }
    }
}
