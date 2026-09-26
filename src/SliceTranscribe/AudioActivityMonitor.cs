using NAudio.CoreAudioApi;

namespace SliceTranscribe;

internal sealed class AudioActivityMonitor
{
    private const string PhoneRadioPauseReason =
        "phone-media";

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

        DateTimeOffset? lastPhoneAudio =
            null;

        bool holdingPhoneRadio =
            false;

        bool startupAudioRecovered =
            false;

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

                bool phoneActive =
                    TryFindPhoneA2dpAudio(
                        manager.Sessions,
                        out string? source,
                        out float peak);

                if (!startupAudioRecovered)
                {
                    startupAudioRecovered =
                        true;

                    if (!phoneActive)
                    {
                        await RadioController.ReleasePauseAsync(
                            "startup-recovery",
                            cancellationToken);
                    }
                }

                if (phoneActive)
                {
                    lastPhoneAudio =
                        now;

                    if (PhoneAudioSessionController.IsMuteRequested)
                    {
                        await PhoneAudioSessionController.EnsureMuteAppliedAsync(
                            cancellationToken);
                    }

                    if (!holdingPhoneRadio)
                    {
                        holdingPhoneRadio =
                            await RadioController.RequestPauseAsync(
                                PhoneRadioPauseReason,
                                cancellationToken);

                        if (holdingPhoneRadio)
                        {
                            Console.WriteLine(
                                $"PHONE MEDIA ACTIVE -> {source} ({peak:P1})");

                            DiagnosticLog.Event(
                                "audio_activity",
                                "phone_media_active",
                                new
                                {
                                    source,
                                    peak
                                });
                        }
                    }
                }
                else if (
                    holdingPhoneRadio &&
                    lastPhoneAudio is not null &&
                    now - lastPhoneAudio.Value >=
                        _resumeDelay)
                {
                    await RadioController.ReleasePauseAsync(
                        PhoneRadioPauseReason,
                        cancellationToken);

                    holdingPhoneRadio =
                        false;

                    lastPhoneAudio =
                        null;

                    DiagnosticLog.Event(
                        "audio_activity",
                        "phone_media_quiet",
                        new
                        {
                            resume_delay_ms =
                                (long)_resumeDelay.TotalMilliseconds
                        });
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
            if (holdingPhoneRadio)
            {
                try
                {
                    await RadioController.ReleasePauseAsync(
                        PhoneRadioPauseReason,
                        CancellationToken.None);
                }
                catch
                {
                }
            }

        }
    }

    private static bool TryFindPhoneA2dpAudio(
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

                string displayName =
                    session.DisplayName ??
                    string.Empty;

                if (!displayName.Contains(
                    "A2DP SNK",
                    StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                float sessionPeak =
                    session.AudioMeterInformation.MasterPeakValue;

                if (sessionPeak <
                    ActivityThreshold)
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
                    displayName;
            }
            catch
            {
                // Sessions can disappear between enumeration and inspection.
            }
        }

        return source is not null;
    }
}
