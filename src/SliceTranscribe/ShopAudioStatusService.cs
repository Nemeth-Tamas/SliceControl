using NAudio.CoreAudioApi;
using System.Diagnostics;

namespace SliceTranscribe;

internal sealed record ShopAudioStatus(
    bool BangOlufsenConnected,
    string ActiveOutput,
    int Volume,
    bool Muted,
    string CurrentSource,
    float MasterLevel,
    string RadioState,
    string? RadioPreset,
    string? RadioStation,
    string RadioNowPlaying,
    string[] Errors);

internal static class ShopAudioStatusService
{
    public static async Task<ShopAudioStatus> CaptureAsync(
        CancellationToken cancellationToken = default)
    {
        var errors =
            new List<string>();

        string outputName =
            "unknown";

        int volume =
            0;

        bool muted =
            false;

        float masterPeak =
            0;

        string source =
            "idle";

        bool boConnected =
            false;

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

            volume =
                (int)Math.Round(
                    output.AudioEndpointVolume.MasterVolumeLevelScalar *
                    100.0f);

            muted =
                output.AudioEndpointVolume.Mute;

            masterPeak =
                output.AudioMeterInformation.MasterPeakValue;

            boConnected =
                outputName.Contains(
                    "Bang & Olufsen",
                    StringComparison.OrdinalIgnoreCase);

            AudioSessionManager manager =
                output.AudioSessionManager;

            manager.RefreshSessions();

            bool phoneActive =
                false;

            bool sonoBusActive =
                false;

            bool radioActive =
                false;

            bool otherActive =
                false;

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

                    float peak =
                        session.AudioMeterInformation.MasterPeakValue;

                    if (session.SimpleAudioVolume.Mute ||
                        peak <
                            0.003f)
                    {
                        continue;
                    }

                    string displayName =
                        session.DisplayName ??
                        string.Empty;

                    if (displayName.Contains(
                        "A2DP SNK",
                        StringComparison.OrdinalIgnoreCase))
                    {
                        phoneActive =
                            true;

                        continue;
                    }

                    uint processId =
                        session.GetProcessID;

                    if (IsSonoBus(
                        processId))
                    {
                        sonoBusActive =
                            true;

                        continue;
                    }

                    if (IsVlc(
                        processId))
                    {
                        radioActive =
                            true;

                        continue;
                    }

                    otherActive =
                        true;
                }
                catch
                {
                }
            }

            source =
                muted
                    ? "muted"
                    : phoneActive
                        ? "phone-a2dp"
                        : sonoBusActive
                            ? "sonobus"
                            : radioActive
                            ? "radio"
                            : otherActive
                                ? "windows-audio"
                                : "idle";
        }
        catch (Exception ex)
        {
            errors.Add(
                ex.Message);
        }

        string radioState =
            "Unknown";

        string nowPlaying =
            "Unknown";

        try
        {
            radioState =
                await RadioController.GetPlaybackStateAsync(
                    cancellationToken);
        }
        catch (Exception ex)
        {
            errors.Add(
                $"Radio state: {ex.Message}");
        }

        try
        {
            nowPlaying =
                await RadioController.GetNowPlayingAsync(
                    cancellationToken);
        }
        catch (Exception ex)
        {
            errors.Add(
                $"Now playing: {ex.Message}");
        }

        return new ShopAudioStatus(
            BangOlufsenConnected:
                boConnected,
            ActiveOutput:
                outputName,
            Volume:
                volume,
            Muted:
                muted,
            CurrentSource:
                source,
            MasterLevel:
                masterPeak,
            RadioState:
                radioState,
            RadioPreset:
                RadioController.CurrentPreset,
            RadioStation:
                RadioController.CurrentStation,
            RadioNowPlaying:
                nowPlaying,
            Errors:
                errors.ToArray());
    }

    private static bool IsSonoBus(
        uint processId)
    {
        if (processId == 0)
        {
            return false;
        }

        try
        {
            using Process process =
                Process.GetProcessById(
                    checked(
                        (int)processId));

            return process.ProcessName.Equals(
                "SonoBus",
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsVlc(
        uint processId)
    {
        if (processId == 0)
        {
            return false;
        }

        try
        {
            using Process process =
                Process.GetProcessById(
                    checked(
                        (int)processId));

            return process.ProcessName.Equals(
                "vlc",
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
