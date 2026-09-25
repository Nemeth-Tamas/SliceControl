using NAudio.CoreAudioApi;

namespace SliceTranscribe;

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
        return SetVolumePercent(
            VolumePercent +
            delta);
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
