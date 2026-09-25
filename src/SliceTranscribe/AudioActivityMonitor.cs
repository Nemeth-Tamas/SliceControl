using NAudio.CoreAudioApi;
using System.Diagnostics;

namespace SliceTranscribe;

internal sealed class AudioActivityMonitor
{
    private const string RadioPauseReason =
        "external-audio";

    private const float ActivityThreshold =
        0.003f;

    private readonly TimeSpan _pollInterval =
        TimeSpan.FromMilliseconds(150);

    private readonly TimeSpan _resumeDelay =
        TimeSpan.FromSeconds(2);

    private readonly TimeSpan _sessionRefreshInterval =
        TimeSpan.FromSeconds(1);

    public async Task RunAsync(
        CancellationToken cancellationToken)
    {
        using var enumerator =
            new MMDeviceEnumerator();

        using MMDevice output =
            enumerator.GetDefaultAudioEndpoint(
                DataFlow.Render,
                Role.Multimedia);

        AudioSessionManager manager =
            output.AudioSessionManager;

        DateTimeOffset lastRefresh =
            DateTimeOffset.MinValue;

        DateTimeOffset? lastExternalAudio =
            null;

        bool holdingRadio =
            false;

        string? activeSource =
            null;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                DateTimeOffset now =
                    DateTimeOffset.UtcNow;

                if (now - lastRefresh >=
                    _sessionRefreshInterval)
                {
                    manager.RefreshSessions();

                    lastRefresh =
                        now;
                }

                bool active =
                    TryFindExternalAudio(
                        manager.Sessions,
                        out string? source,
                        out float peak);

                if (active)
                {
                    lastExternalAudio =
                        now;

                    if (!holdingRadio)
                    {
                        holdingRadio =
                            await RadioController.RequestPauseAsync(
                                RadioPauseReason,
                                cancellationToken);

                        if (holdingRadio)
                        {
                            activeSource =
                                source;

                            Console.WriteLine(
                                $"AUDIO PRIORITY -> {source} ({peak:P1})");
                        }
                    }
                    else if (!string.Equals(
                        activeSource,
                        source,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        activeSource =
                            source;

                        Console.WriteLine(
                            $"AUDIO PRIORITY -> {source} ({peak:P1})");
                    }
                }
                else if (
                    holdingRadio &&
                    lastExternalAudio is not null &&
                    now - lastExternalAudio.Value >=
                        _resumeDelay)
                {
                    await RadioController.ReleasePauseAsync(
                        RadioPauseReason,
                        cancellationToken);

                    holdingRadio =
                        false;

                    activeSource =
                        null;

                    lastExternalAudio =
                        null;
                }

                await Task.Delay(
                    _pollInterval,
                    cancellationToken);
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            if (holdingRadio)
            {
                try
                {
                    await RadioController.ReleasePauseAsync(
                        RadioPauseReason,
                        CancellationToken.None);
                }
                catch
                {
                }
            }
        }
    }

    private static bool TryFindExternalAudio(
        SessionCollection sessions,
        out string? source,
        out float peak)
    {
        source =
            null;

        peak =
            0;

        for (int i = 0;
             i < sessions.Count;
             i++)
        {
            try
            {
                using AudioSessionControl session =
                    sessions[i];

                float sessionPeak =
                    session.AudioMeterInformation.MasterPeakValue;

                if (sessionPeak <
                    ActivityThreshold)
                {
                    continue;
                }

                uint processId =
                    session.GetProcessID;

                string processName =
                    GetProcessName(
                        processId);

                if (string.Equals(
                    processName,
                    "vlc",
                    StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (sessionPeak <=
                    peak)
                {
                    continue;
                }

                peak =
                    sessionPeak;

                source =
                    DescribeSession(
                        session,
                        processId,
                        processName);
            }
            catch
            {
                // Sessions can disappear between enumeration and inspection.
            }
        }

        return source is not null;
    }

    private static string DescribeSession(
        AudioSessionControl session,
        uint processId,
        string processName)
    {
        string displayName =
            string.Empty;

        try
        {
            displayName =
                session.DisplayName;
        }
        catch
        {
        }

        if (!string.IsNullOrWhiteSpace(
            displayName))
        {
            return $"{displayName} / {processName} ({processId})";
        }

        return $"{processName} ({processId})";
    }

    private static string GetProcessName(
        uint processId)
    {
        if (processId == 0)
        {
            return "Windows audio";
        }

        try
        {
            using Process process =
                Process.GetProcessById(
                    checked((int)processId));

            return process.ProcessName;
        }
        catch
        {
            return $"PID {processId}";
        }
    }
}
