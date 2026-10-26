using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace SliceMcp;

[McpServerToolType]
public sealed class EagleEyeImageTools(EagleEyeCamera camera)
{
    [McpServerTool, Description("Take a fresh on-demand EagleEye USB JPEG and return native MCP image/jpeg content plus PTZ/FOV metadata. max_width 320..1920, default 1280. No background capture or saved photographs. Returns camera_busy if another action is active.")]
    public Task<CallToolResult> camera_snapshot(int max_width = 1280, CancellationToken cancellationToken = default)
        => camera.Snapshot(max_width, cancellationToken);

    [McpServerTool, Description("Wait for camera idle, then atomically read PTZ/FOV and take a fresh JPEG. One shared lock excludes competing motion and capture. timeout_seconds >0..30; max_width 320..1920.")]
    public Task<CallToolResult> camera_observe(double timeout_seconds = 15, int max_width = 1280, CancellationToken cancellationToken = default)
        => camera.Snapshot(max_width, cancellationToken, wait: true, timeout: timeout_seconds);

    [McpServerTool, Description("Atomically move optional absolute PTZ axes, wait for estimated settlement, and return a fresh JPEG with readback. Unspecified axes stay unchanged. Optional settle_delay_seconds 0..15 can lengthen the calculated minimum. max_width 320..1920.")]
    public Task<CallToolResult> camera_snapshot_after_move(double? pan_degrees = null, double? tilt_degrees = null,
        double? zoom_percent = null, double? settle_delay_seconds = null, int max_width = 1280, CancellationToken cancellationToken = default)
        => camera.SetView(pan_degrees, tilt_degrees, zoom_percent, cancellationToken, snapshot: true, settle: settle_delay_seconds, maxWidth: max_width);

    [McpServerTool, Description("Compatibility alias for camera_snapshot: one fresh on-demand USB EagleEye JPEG, returned as native MCP image content.")]
    public Task<CallToolResult> eagleeye_snapshot(CancellationToken cancellationToken = default) => camera.Snapshot(1280, cancellationToken);
}
