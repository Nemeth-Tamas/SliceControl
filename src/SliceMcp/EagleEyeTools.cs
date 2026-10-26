using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace SliceMcp;

[McpServerToolType]
public sealed class EagleEyeTools(EagleEyeCamera camera)
{
    [McpServerTool, Description("Read fresh EagleEye USB pan/tilt degrees, zoom percent, autofocus, native ranges and estimated/calibrated FOV. Does not move the camera.")]
    public Task<CallToolResult> camera_get_state(CancellationToken cancellationToken = default) => camera.GetState(cancellationToken);

    [McpServerTool, Description("Camera health, busy/active operation, last error, uptime and PTZ/FOV. While busy returns the last observed state without interrupting motion/capture.")]
    public Task<CallToolResult> camera_status(CancellationToken cancellationToken = default) => camera.Status(cancellationToken);

    [McpServerTool, Description("Move to absolute pan and tilt in driver-reported degrees; optionally set zoom 0..100. Rejects out-of-range inputs before moving; returns camera_busy if occupied. Waits an estimated settle interval and returns readback.")]
    public Task<CallToolResult> camera_set_view(double pan_degrees, double tilt_degrees, double? zoom_percent = null, CancellationToken cancellationToken = default)
        => camera.SetView(pan_degrees, tilt_degrees, zoom_percent, cancellationToken);

    [McpServerTool, Description("Relative PTZ move: at most +/-30 pan/tilt degrees and +/-25 zoom percentage points per call, clamped to device limits. zoom_aware scales angular deltas by current FOV. Returns camera_busy rather than queueing moves.")]
    public Task<CallToolResult> camera_nudge(double pan_delta_degrees = 0, double tilt_delta_degrees = 0, double zoom_delta_percent = 0, bool zoom_aware = false, CancellationToken cancellationToken = default)
        => camera.Nudge(pan_delta_degrees, tilt_delta_degrees, zoom_delta_percent, zoom_aware, cancellationToken);

    [McpServerTool, Description("Center a target seen in a fresh uncropped snapshot: image_x=-1 left/+1 right; image_y=-1 top/+1 bottom; (0,0) is center. Uses current zoom and perspective FOV geometry. Default FOV model is estimated; re-observe and refine. Driver limits/whole-degree precision may prevent exact centering.")]
    public Task<CallToolResult> camera_center_on(double image_x, double image_y, CancellationToken cancellationToken = default)
        => camera.CenterOn(image_x, image_y, cancellationToken);

    [McpServerTool, Description("Set optical zoom position 0..100 percent of the native driver range, preserving pan/tilt. This is position percentage, not a measured magnification ratio.")]
    public Task<CallToolResult> camera_set_zoom(double zoom_percent, CancellationToken cancellationToken = default)
        => camera.SetView(null, null, zoom_percent, cancellationToken);

    [McpServerTool, Description("Return pan/tilt to their driver-reported neutral defaults and fully zoom out. Does not recall a vendor preset.")]
    public Task<CallToolResult> camera_home(CancellationToken cancellationToken = default) => camera.Home(cancellationToken);

    [McpServerTool, Description("Wait until the shared camera operation and estimated settle interval finish, then return fresh PTZ state. timeout_seconds: greater than 0, at most 30.")]
    public Task<CallToolResult> camera_wait_until_idle(double timeout_seconds = 15, CancellationToken cancellationToken = default)
        => camera.WaitUntilIdle(timeout_seconds, cancellationToken);

    [McpServerTool, Description("Show zoom/FOV calibration and coordinate conventions. Manufacturer-endpoint zoom interpolation is explicitly marked estimated until a measured table is saved.")]
    public Task<CallToolResult> camera_get_calibration(CancellationToken cancellationToken = default) => camera.GetCalibration(cancellationToken);

    [McpServerTool, Description("Save a measured zoom/FOV table of 2..30 points, including zoom_percent 0 and 100. Each point has zoom_percent, horizontal_fov_degrees, vertical_fov_degrees. Zoom increases, FOV cannot increase. Prior table is backed up; rollback=true restores it. Use fov_table or rollback, not both.")]
    public Task<CallToolResult> camera_set_calibration(CameraFovPoint[]? fov_table = null, bool rollback = false, CancellationToken cancellationToken = default)
        => camera.SetCalibration(fov_table, rollback, cancellationToken);

    [McpServerTool, Description("Compatibility alias: fresh EagleEye USB PTZ status and ranges, without movement.")]
    public Task<CallToolResult> eagleeye_status(CancellationToken cancellationToken = default) => camera.GetState(cancellationToken);

    [McpServerTool, Description("Compatibility tool: nudge pan/tilt/zoom by -5..5 native steps. This EagleEye driver reports zero pan/tilt step; use a documented whole-degree fallback. Returns camera_busy if occupied.")]
    public Task<CallToolResult> eagleeye_nudge(string axis, int steps, CancellationToken cancellationToken = default)
        => camera.LegacyNudge(axis, steps, cancellationToken);

    [McpServerTool, Description("Compatibility tool: move pan/tilt to the midpoint of their reported ranges. For neutral pan/tilt plus wide zoom use camera_home; for an image target use camera_center_on.")]
    public Task<CallToolResult> eagleeye_center(CancellationToken cancellationToken = default) => camera.Home(cancellationToken, midpoint: true);
}
