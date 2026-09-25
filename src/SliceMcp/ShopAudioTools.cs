using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json;

namespace SliceMcp;

[McpServerToolType]
internal static class ShopAudioTools
{
    [McpServerTool]
    [Description(
        "Returns B&O output connection state, master volume/mute, active Windows output, current source, audio level, radio state, now-playing metadata, and errors.")]
    public static Task<JsonElement> audio_status(
        SliceApiClient api)
    {
        return api.GetAsync(
            "/api/audio/status");
    }

    [McpServerTool]
    [Description(
        "Sets the shop master output volume to an absolute percentage from 0 to 100.")]
    public static Task<JsonElement> audio_set_volume(
        SliceApiClient api,
        [Description(
            "Target master volume percentage from 0 to 100.")]
        int volume)
    {
        return api.PostAsync(
            $"/api/audio/volume?value={Math.Clamp(volume, 0, 100)}");
    }

    [McpServerTool]
    [Description(
        "Adjusts the shop master output volume by a signed percentage-point delta.")]
    public static Task<JsonElement> audio_adjust_volume(
        SliceApiClient api,
        [Description(
            "Signed volume change in percentage points, for example 5 or -10.")]
        int delta)
    {
        int clamped =
            Math.Clamp(
                delta,
                -100,
                100);

        return api.PostAsync(
            $"/api/audio/adjust?delta={clamped}");
    }

    [McpServerTool]
    [Description(
        "Sets or clears the shop master output mute.")]
    public static Task<JsonElement> audio_set_mute(
        SliceApiClient api,
        [Description(
            "True to mute the full shop output, false to unmute it.")]
        bool muted)
    {
        return api.PostAsync(
            $"/api/audio/mute?value={muted.ToString().ToLowerInvariant()}");
    }

    [McpServerTool]
    [Description(
        "Lists the named high-level shop audio presets and their configured volume, mute, and radio actions.")]
    public static Task<JsonElement> audio_list_presets(
        SliceApiClient api)
    {
        return api.GetAsync(
            "/api/audio/presets");
    }

    [McpServerTool]
    [Description(
        "Activates a named shop audio preset such as shop-daytime, quiet, closing-time, customer-demo, or night.")]
    public static Task<JsonElement> audio_activate_preset(
        SliceApiClient api,
        [Description(
            "Name of the configured audio preset.")]
        string name)
    {
        return api.PostAsync(
            "/api/audio/preset?name=" +
            Uri.EscapeDataString(
                name));
    }

    [McpServerTool]
    [Description(
        "Lists configured online-radio presets and their stream URLs.")]
    public static Task<JsonElement> radio_list_presets(
        SliceApiClient api)
    {
        return api.GetAsync(
            "/api/radio/presets");
    }

    [McpServerTool]
    [Description(
        "Starts online radio using a configured preset name or a direct HTTP/HTTPS stream URL.")]
    public static Task<JsonElement> radio_play(
        SliceApiClient api,
        [Description(
            "Configured preset name such as retro, or a direct HTTP/HTTPS radio stream URL.")]
        string preset_or_url)
    {
        return api.PostAsync(
            "/api/radio/play?value=" +
            Uri.EscapeDataString(
                preset_or_url));
    }

    [McpServerTool]
    [Description(
        "Stops the currently playing VLC online-radio stream.")]
    public static Task<JsonElement> radio_stop(
        SliceApiClient api)
    {
        return api.PostAsync(
            "/api/radio/stop");
    }

    [McpServerTool]
    [Description(
        "Returns VLC radio transport state and best-effort station or track now-playing metadata.")]
    public static Task<JsonElement> radio_now_playing(
        SliceApiClient api)
    {
        return api.GetAsync(
            "/api/radio/now-playing");
    }

    [McpServerTool]
    [Description(
        "Returns the current Slice recording state used by the physical GREEN/MUTE/RED controls and the remote web UI.")]
    public static Task<JsonElement> recording_status(
        SliceApiClient api)
    {
        return api.GetAsync(
            "/api/status");
    }

    [McpServerTool]
    [Description(
        "Starts the normal shop microphone recording and transcription path, including radio and phone-audio ducking.")]
    public static Task<JsonElement> recording_start(
        SliceApiClient api)
    {
        return api.PostAsync(
            "/api/record/start");
    }

    [McpServerTool]
    [Description(
        "Pauses or resumes the current shop microphone recording.")]
    public static Task<JsonElement> recording_pause_resume(
        SliceApiClient api)
    {
        return api.PostAsync(
            "/api/record/pause");
    }

    [McpServerTool]
    [Description(
        "Stops and finalizes the current shop microphone recording and transcript.")]
    public static Task<JsonElement> recording_stop(
        SliceApiClient api)
    {
        return api.PostAsync(
            "/api/record/stop");
    }
}
