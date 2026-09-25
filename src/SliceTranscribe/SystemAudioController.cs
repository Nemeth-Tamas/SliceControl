using NAudio.CoreAudioApi;

namespace SliceTranscribe;

internal static class SystemAudioController
{
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

    private static MMDevice GetDefaultRenderDevice()
    {
        using var enumerator =
            new MMDeviceEnumerator();

        return enumerator.GetDefaultAudioEndpoint(
            DataFlow.Render,
            Role.Multimedia);
    }
}
