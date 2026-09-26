using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json;

namespace SliceMcp;

[McpServerToolType]
public sealed class ShopAudioTools
{
    private readonly SliceApiClient _api;

    public ShopAudioTools(
        SliceApiClient api)
    {
        _api =
            api;
    }

    [McpServerTool]
    [Description(
        "Returns B&O output connection state, master volume/mute, active Windows output, current source, audio level, radio state, now-playing metadata, and errors.")]
    public Task<JsonElement> audio_status()
    {
        return _api.GetAsync(
            "/api/audio/status");
    }

    [McpServerTool]
    [Description(
        "Sets the shop master output volume to an absolute percentage from 0 to 100.")]
    public Task<JsonElement> audio_set_volume(
        [Description(
            "Target master volume percentage from 0 to 100.")]
        int volume)
    {
        return _api.PostAsync(
            $"/api/audio/volume?value={Math.Clamp(volume, 0, 100)}");
    }

    [McpServerTool]
    [Description(
        "Adjusts the shop master output volume by a signed percentage-point delta.")]
    public Task<JsonElement> audio_adjust_volume(
        [Description(
            "Signed volume change in percentage points, for example 5 or -10.")]
        int delta)
    {
        int clamped =
            Math.Clamp(
                delta,
                -100,
                100);

        return _api.PostAsync(
            $"/api/audio/adjust?delta={clamped}");
    }

    [McpServerTool]
    [Description(
        "Sets or clears the shop master output mute.")]
    public Task<JsonElement> audio_set_mute(
        [Description(
            "True to mute the full shop output, false to unmute it.")]
        bool muted)
    {
        return _api.PostAsync(
            $"/api/audio/mute?value={muted.ToString().ToLowerInvariant()}");
    }

    [McpServerTool]
    [Description(
        "Lists the named high-level shop audio presets and their configured volume, mute, and radio actions.")]
    public Task<JsonElement> audio_list_presets()
    {
        return _api.GetAsync(
            "/api/audio/presets");
    }

    [McpServerTool]
    [Description(
        "Activates a named shop audio preset such as shop-daytime, quiet, closing-time, customer-demo, or night.")]
    public Task<JsonElement> audio_activate_preset(
        [Description(
            "Name of the configured audio preset.")]
        string name)
    {
        return _api.PostAsync(
            "/api/audio/preset?name=" +
            Uri.EscapeDataString(
                name));
    }

    [McpServerTool]
    [Description(
        "Lists configured online-radio presets and their stream URLs.")]
    public Task<JsonElement> radio_list_presets()
    {
        return _api.GetAsync(
            "/api/radio/presets");
    }

    [McpServerTool]
    [Description(
        "Starts online radio using a configured preset name or a direct HTTP/HTTPS stream URL.")]
    public Task<JsonElement> radio_play(
        [Description(
            "Configured preset name such as retro, or a direct HTTP/HTTPS radio stream URL.")]
        string preset_or_url)
    {
        return _api.PostAsync(
            "/api/radio/play?value=" +
            Uri.EscapeDataString(
                preset_or_url));
    }

    [McpServerTool]
    [Description(
        "Stops the currently playing VLC online-radio stream.")]
    public Task<JsonElement> radio_stop()
    {
        return _api.PostAsync(
            "/api/radio/stop");
    }

    [McpServerTool]
    [Description(
        "Returns VLC radio transport state and best-effort station or track now-playing metadata.")]
    public Task<JsonElement> radio_now_playing()
    {
        return _api.GetAsync(
            "/api/radio/now-playing");
    }

    [McpServerTool]
    [Description(
        "Returns the current Slice recording state used by the physical GREEN/MUTE/RED controls and the remote web UI.")]
    public Task<JsonElement> recording_status()
    {
        return _api.GetAsync(
            "/api/status");
    }

    [McpServerTool]
    [Description(
        "Starts the normal shop microphone recording and transcription path, including radio and phone-audio ducking.")]
    public Task<JsonElement> recording_start()
    {
        return _api.PostAsync(
            "/api/record/start");
    }

    [McpServerTool]
    [Description(
        "Pauses or resumes the current shop microphone recording.")]
    public Task<JsonElement> recording_pause_resume()
    {
        return _api.PostAsync(
            "/api/record/pause");
    }

    [McpServerTool]
    [Description(
        "Stops and finalizes the current shop microphone recording and transcript.")]
    public Task<JsonElement> recording_stop()
    {
        return _api.PostAsync(
            "/api/record/stop");
    }

    [McpServerTool]
    [Description(
        "Returns the ECHO puck assistant state, Hermes configuration/session state, and the last voice command, reply, or error.")]
    public Task<JsonElement> assistant_status()
    {
        return _api.GetAsync(
            "/api/assistant/status");
    }

    [McpServerTool]
    [Description(
        "Enables or disables local ECHO wake-word listening on the Slice.")]
    public Task<JsonElement> assistant_set_enabled(
        [Description(
            "True enables wake-word listening; false disables it.")]
        bool enabled)
    {
        return _api.PostAsync(
            $"/api/assistant/enabled?value={enabled.ToString().ToLowerInvariant()}");
    }

}
