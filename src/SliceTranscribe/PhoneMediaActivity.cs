using NAudio.CoreAudioApi;

namespace SliceTranscribe;

internal static class PhoneMediaActivity
{
    private const float ActivityThreshold =
        0.003f;

    public static bool IsActive()
    {
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
                        session.DisplayName ?? string.Empty;

                    if (!displayName.Contains(
                            "A2DP SNK",
                            StringComparison.OrdinalIgnoreCase) &&
                        !displayName.Contains(
                            "iPhone",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (session.AudioMeterInformation.MasterPeakValue >=
                        ActivityThreshold)
                    {
                        return true;
                    }
                }
                catch
                {
                }
            }
        }
        catch
        {
        }

        return false;
    }
}
