using NAudio.CoreAudioApi;

namespace SliceTranscribe;

internal sealed record VolumeAdjustmentResult(
    int Before,
    int RequestedDelta,
    int Target,
    int After);

internal static class SystemAudioController
{
    public static int VolumePercent
    {
        get
        {
            using MMDevice device =
                GetDefaultRenderDevice();

            return (int)Math.Round(
                device.AudioEndpointVolume.MasterVolumeLevelScalar *
                100.0f);
        }
    }

    public static string DefaultOutputName
    {
        get
        {
            using MMDevice device =
                GetDefaultRenderDevice();

            return device.FriendlyName;
        }
    }

    public static float MasterPeak
    {
        get
        {
            using MMDevice device =
                GetDefaultRenderDevice();

            return device.AudioMeterInformation.MasterPeakValue;
        }
    }

    public static bool IsMuted
    {
        get
        {
            using MMDevice device =
                GetDefaultRenderDevice();

            return device.AudioEndpointVolume.Mute;
        }
    }

    public static bool ToggleMute()
    {
        using MMDevice device =
            GetDefaultRenderDevice();

        bool muted =
            !device.AudioEndpointVolume.Mute;

        device.AudioEndpointVolume.Mute =
            muted;

        return muted;
    }

    public static void SetMuted(
        bool muted)
    {
        using MMDevice device =
            GetDefaultRenderDevice();

        device.AudioEndpointVolume.Mute =
            muted;
    }

    public static int SetVolumePercent(
        int percent)
    {
        int clamped =
            Math.Clamp(
                percent,
                0,
                100);

        using MMDevice device =
            GetDefaultRenderDevice();

        device.AudioEndpointVolume.MasterVolumeLevelScalar =
            clamped /
            100.0f;

        return clamped;
    }

    public static int AdjustVolumePercent(
        int delta)
    {
        return AdjustVolumePercentDetailed(
            delta).After;
    }

    public static VolumeAdjustmentResult AdjustVolumePercentDetailed(
        int delta)
    {
        using MMDevice device =
            GetDefaultRenderDevice();

        int before =
            (int)Math.Round(
                device.AudioEndpointVolume.MasterVolumeLevelScalar *
                100.0f);

        int target =
            Math.Clamp(
                before +
                delta,
                0,
                100);

        device.AudioEndpointVolume.MasterVolumeLevelScalar =
            target /
            100.0f;

        int after =
            (int)Math.Round(
                device.AudioEndpointVolume.MasterVolumeLevelScalar *
                100.0f);

        return new VolumeAdjustmentResult(
            Before:
                before,
            RequestedDelta:
                delta,
            Target:
                target,
            After:
                after);
    }

    private static MMDevice GetDefaultRenderDevice()
    {
        using var enumerator =
            new MMDeviceEnumerator();

        return enumerator.GetDefaultAudioEndpoint(
            DataFlow.Render,
            Role.Multimedia);
    }
}
