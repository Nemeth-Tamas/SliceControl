using NAudio.CoreAudioApi;

namespace SliceTranscribe;

internal static class PhoneAudioSessionController
{
    private static readonly SemaphoreSlim Gate =
        new(
            1,
            1);

    private static readonly HashSet<string> MuteReasons =
        new(
            StringComparer.OrdinalIgnoreCase);

    public static bool IsMuteRequested
    {
        get
        {
            lock (MuteReasons)
            {
                return MuteReasons.Count != 0;
            }
        }
    }

    public static async Task<bool> RequestMuteAsync(
        string reason,
        CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(
            cancellationToken);

        try
        {
            lock (MuteReasons)
            {
                MuteReasons.Add(
                    reason);
            }

            await SetA2dpMutedAsync(
                true,
                cancellationToken);

            Console.WriteLine(
                $"PHONE AUDIO -> muted ({FormatReasons()})");

            return true;
        }
        finally
        {
            Gate.Release();
        }
    }

    public static async Task<bool> ReleaseMuteAsync(
        string reason,
        CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(
            cancellationToken);

        try
        {
            bool stillHeld;

            lock (MuteReasons)
            {
                MuteReasons.Remove(
                    reason);

                stillHeld =
                    MuteReasons.Count != 0;
            }

            if (stillHeld)
            {
                return true;
            }

            await SetA2dpMutedAsync(
                false,
                cancellationToken);

            Console.WriteLine(
                "PHONE AUDIO -> unmuted");

            return true;
        }
        finally
        {
            Gate.Release();
        }
    }

    public static async Task EnsureMuteAppliedAsync(
        CancellationToken cancellationToken = default)
    {
        if (!IsMuteRequested)
        {
            return;
        }

        await SetA2dpMutedAsync(
            true,
            cancellationToken);
    }

    private static string FormatReasons()
    {
        lock (MuteReasons)
        {
            return string.Join(
                ", ",
                MuteReasons);
        }
    }

    private static Task SetA2dpMutedAsync(
        bool muted,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            using var enumerator =
                new MMDeviceEnumerator();

            using MMDevice output =
                enumerator.GetDefaultAudioEndpoint(
                    DataFlow.Render,
                    Role.Multimedia);

            AudioSessionManager manager =
                output.AudioSessionManager;

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

                    if (!displayName.Contains(
                        "A2DP SNK",
                        StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    session.SimpleAudioVolume.Mute =
                        muted;
                }
                catch
                {
                    // Audio sessions can disappear while they are enumerated.
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"PHONE AUDIO -> session mute failed: {ex.Message}");
        }

        return Task.CompletedTask;
    }
}
