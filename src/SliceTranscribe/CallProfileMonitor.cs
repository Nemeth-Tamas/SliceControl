using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace SliceTranscribe;

internal sealed class CallProfileMonitor
{
    private const string RadioPauseReason =
        "phone-call";

    private readonly PhoneAudioManager _phoneAudio;

    private readonly TimeSpan _pollInterval =
        TimeSpan.FromMilliseconds(100);

    private readonly TimeSpan _releaseDelay =
        TimeSpan.FromSeconds(2);

    public CallProfileMonitor(
        PhoneAudioManager phoneAudio)
    {
        _phoneAudio =
            phoneAudio;
    }

    public async Task RunAsync(
        CancellationToken cancellationToken)
    {
        bool callProfileActive =
            false;

        int consecutiveActiveSamples =
            0;

        DateTimeOffset? lastActive =
            null;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                bool hfpActive =
                    IsIPhoneHandsFreeActive();

                DateTimeOffset now =
                    DateTimeOffset.UtcNow;

                if (hfpActive)
                {
                    consecutiveActiveSamples++;

                    lastActive =
                        now;

                    if (!callProfileActive &&
                        consecutiveActiveSamples >= 2)
                    {
                        callProfileActive =
                            true;

                        Console.WriteLine(
                            "PHONE CALL -> HFP active; releasing A2DP sink");

                        _phoneAudio.SetSuspendedForCall(
                            true);

                        await RadioController.RequestPauseAsync(
                            RadioPauseReason,
                            cancellationToken);
                    }
                }
                else
                {
                    consecutiveActiveSamples =
                        0;

                    if (
                        callProfileActive &&
                        lastActive is not null &&
                        now - lastActive.Value >=
                            _releaseDelay)
                    {
                        callProfileActive =
                            false;

                        Console.WriteLine(
                            "PHONE CALL -> HFP inactive; returning to normal audio");

                        _phoneAudio.SetSuspendedForCall(
                            false);

                        await RadioController.ReleasePauseAsync(
                            RadioPauseReason,
                            cancellationToken);

                        lastActive =
                            null;
                    }
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
            if (callProfileActive)
            {
                _phoneAudio.SetSuspendedForCall(
                    false);

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

    private static bool IsIPhoneHandsFreeActive()
    {
        try
        {
            using var enumerator =
                new MMDeviceEnumerator();

            MMDeviceCollection devices =
                enumerator.EnumerateAudioEndPoints(
                    DataFlow.Render,
                    DeviceState.Active);

            for (int d = 0;
                 d < devices.Count;
                 d++)
            {
                using MMDevice device =
                    devices[d];

                AudioSessionManager manager =
                    device.AudioSessionManager;

                manager.RefreshSessions();

                SessionCollection sessions =
                    manager.Sessions;

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

                        string combinedName =
                            $"{device.FriendlyName} {displayName}";

                        if (!combinedName.Contains(
                                "iPhone",
                                StringComparison.OrdinalIgnoreCase) ||
                            !combinedName.Contains(
                                "Hands-Free",
                                StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        if (session.State ==
                            AudioSessionState.AudioSessionStateActive)
                        {
                            return true;
                        }
                    }
                    catch
                    {
                    }
                }
            }
        }
        catch
        {
        }

        return false;
    }
}
