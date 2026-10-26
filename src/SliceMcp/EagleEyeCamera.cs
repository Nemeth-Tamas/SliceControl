using System.Diagnostics;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Protocol;
using OpenCvSharp;

namespace SliceMcp;

public sealed record CameraFovPoint(
    [property: JsonPropertyName("zoom_percent")] double ZoomPercent,
    [property: JsonPropertyName("horizontal_fov_degrees")] double HorizontalFovDegrees,
    [property: JsonPropertyName("vertical_fov_degrees")] double VerticalFovDegrees);

internal sealed record CameraState(string Camera, int DeviceIndex, DateTimeOffset ObservedAtUtc,
    double? PanDegrees, double? TiltDegrees, double? ZoomPercent, bool? Autofocus,
    object Fov, Dictionary<string, CameraAxis> Axes);

/// <summary>One gate for PTZ, settling, calibration and snapshots, shared by all MCP aliases.</summary>
public sealed class EagleEyeCamera : IDisposable
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly BlockingCollection<Action> operations = new();
    private readonly Thread worker;
    private EagleEyeDirectShow? device;
    private readonly Stopwatch uptime = Stopwatch.StartNew();
    private readonly string calibrationPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SliceAppliance", "eagleeye-fov.json");
    private CameraFovPoint[]? calibration;
    private CameraState? lastState;
    private string? lastError;
    private string? activeOperation;

    public EagleEyeCamera()
    {
        worker = new Thread(() =>
        {
            try { foreach (var operation in operations.GetConsumingEnumerable()) operation(); }
            finally { device?.Dispose(); device = null; }
        }) { IsBackground = true, Name = "EagleEye-Camera-STA" };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
        if (File.Exists(calibrationPath))
        {
            try
            {
                var saved = JsonSerializer.Deserialize<CameraFovPoint[]>(File.ReadAllText(calibrationPath), Json);
                calibration = saved is null ? null : ValidateCalibration(saved);
            }
            catch (Exception e) { lastError = "Saved FOV calibration could not be loaded; using estimated model: " + e.Message; }
        }
    }

    public void Dispose()
    {
        operations.CompleteAdding();
        worker.Join(TimeSpan.FromSeconds(2));
    }

    private EagleEyeDirectShow Device => device ??= EagleEyeDirectShow.Open();

    private static CallToolResult Text(object value) => new() { Content = [new TextContentBlock { Text = JsonSerializer.Serialize(value, Json) }] };
    private static CallToolResult Error(string code, string message) => new()
        { IsError = true, Content = [new TextContentBlock { Text = JsonSerializer.Serialize(new { error = code, message }, Json) }] };

    private async Task<CallToolResult> Run(string name, Func<CancellationToken, CallToolResult> operation,
        CancellationToken cancellationToken, bool wait = false, double timeoutSeconds = 15)
    {
        if (!double.IsFinite(timeoutSeconds) || timeoutSeconds <= 0 || timeoutSeconds > 30)
            return Error("invalid_arguments", "timeout_seconds must be greater than 0 and at most 30.");
        try
        {
            if (!await gate.WaitAsync(wait ? TimeSpan.FromSeconds(timeoutSeconds) : TimeSpan.Zero, cancellationToken))
                return Error("camera_busy", "Another camera operation or settle interval is active; retry or use camera_wait_until_idle.");
        }
        catch (OperationCanceledException) { return Error("cancelled", "Camera request cancelled before acquiring the camera."); }

        var completion = new TaskCompletionSource<CallToolResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        stop.CancelAfter(TimeSpan.FromSeconds(30));
        activeOperation = name;
        Action action = () =>
        {
            try { stop.Token.ThrowIfCancellationRequested(); completion.TrySetResult(operation(stop.Token)); }
            catch (OperationCanceledException) { completion.TrySetResult(Error("cancelled", "Camera operation cancelled; native I/O may take time to return.")); }
            catch (Exception e)
            {
                lastError = e.GetBaseException().Message;
                completion.TrySetResult(Error(e is ArgumentException ? "invalid_arguments" : "camera_error", lastError));
            }
            finally { activeOperation = null; gate.Release(); stop.Dispose(); }
        };
        try { operations.Add(action); }
        catch (Exception e) { activeOperation = null; gate.Release(); stop.Dispose(); return Error("camera_error", e.Message); }
        try { return await completion.Task.WaitAsync(TimeSpan.FromSeconds(31), cancellationToken); }
        catch (TimeoutException) { return Error("camera_timeout", "Native camera I/O did not return. The camera remains locked until it completes; no additional move will be queued."); }
        catch (OperationCanceledException) { return Error("cancelled", "Camera request cancelled. Any native I/O already in progress must finish before another operation can start."); }
    }

    public Task<CallToolResult> Status(CancellationToken ct)
    {
        if (gate.CurrentCount == 0)
            return Task.FromResult(Text(new { busy = true, active_operation = activeOperation, state = lastState, last_error = lastError, uptime_seconds = uptime.Elapsed.TotalSeconds }));
        return Run("status", _ => Text(new { busy = false, state = ReadState(), last_error = lastError, uptime_seconds = uptime.Elapsed.TotalSeconds }), ct);
    }

    public Task<CallToolResult> GetState(CancellationToken ct) => Run("get_state", _ => Text(ReadState()), ct);

    private CameraState ReadState()
    {
        var selected = Device;
        var axes = selected.ReadAxes();
        var pan = axes["pan"]; var tilt = axes["tilt"]; var zoom = axes["zoom"]; var focus = axes["focus"];
        double? percent = zoom.Supported && zoom.Value.HasValue ? CameraGeometry.ZoomPercent(zoom.Value.Value, zoom.Min, zoom.Max) : null;
        var fov = Fov(percent ?? 0);
        return lastState = new CameraState(selected.Name, selected.Index, DateTimeOffset.UtcNow,
            pan.Supported ? pan.Value : null, tilt.Supported ? tilt.Value : null, percent,
            focus.Supported ? (focus.Flags & 1) != 0 : null,
            new { horizontal_degrees = fov.Horizontal, vertical_degrees = fov.Vertical,
                estimated = calibration is null, source = calibration is null ? "manufacturer-endpoint model; zoom interpolation is not empirically calibrated" : "saved calibration" }, axes);
    }

    private (double Horizontal, double Vertical) Fov(double zoom)
    {
        var points = calibration;
        if (points is null) return CameraGeometry.EstimatedFov(zoom);
        for (int i = 1; i < points.Length; i++)
        {
            if (zoom > points[i].ZoomPercent) continue;
            var a = points[i - 1]; var b = points[i];
            double p = Math.Clamp((zoom - a.ZoomPercent) / (b.ZoomPercent - a.ZoomPercent), 0, 1);
            return (a.HorizontalFovDegrees + p * (b.HorizontalFovDegrees - a.HorizontalFovDegrees),
                a.VerticalFovDegrees + p * (b.VerticalFovDegrees - a.VerticalFovDegrees));
        }
        return (points[^1].HorizontalFovDegrees, points[^1].VerticalFovDegrees);
    }

    private static CameraAxis Writable(CameraState state, string axis)
    {
        var property = state.Axes[axis];
        if (!property.Supported || !property.Manual || !property.Value.HasValue)
            throw new InvalidOperationException($"{axis} is not supported, manually controllable and readable; no move sent.");
        return property;
    }

    private static int Target(CameraState state, string axis, double value, bool clamp)
    {
        CameraGeometry.Finite(value, axis);
        var property = Writable(state, axis);
        if (axis == "zoom")
        {
            if (!clamp && (value < 0 || value > 100)) throw new ArgumentException("zoom_percent must be 0..100.");
            value = property.Min + Math.Clamp(value, 0, 100) / 100 * ((double)property.Max - property.Min);
        }
        else if (!clamp && (value < property.Min || value > property.Max))
            throw new ArgumentException($"{axis} must be {property.Min}..{property.Max} degrees.");
        return CameraGeometry.Align(value, property.Min, property.Max, property.Step);
    }

    private CameraState Move(CameraState before, Dictionary<string, double> requested, bool clamp, CancellationToken ct, double? settle = null)
    {
        // Validate all targets before touching any axis, so invalid multi-axis requests cannot partly move.
        var targets = requested.ToDictionary(p => p.Key, p => Target(before, p.Key, p.Value, clamp));
        double wait = 0;
        {
            var selected = Device;
            if (selected.Name != before.Camera || selected.Index != before.DeviceIndex) throw new InvalidOperationException("Camera selection changed; no move sent.");
            foreach (var (axis, target) in targets)
            {
                ct.ThrowIfCancellationRequested();
                var property = before.Axes[axis];
                if (target == property.Value) continue;
                selected.Set(axis, target);
                double delta = Math.Abs(target - property.Value!.Value);
                wait = Math.Max(wait, axis == "zoom" ? delta / Math.Max(1, property.Max - property.Min) * 5 : delta / 35);
            }
        }
        // IAMCameraControl readback may expose the commanded value before motors arrive.
        // Keep capture and other moves excluded during a conservative estimated settle interval.
        wait = wait > 0 ? Math.Clamp(wait + 1.8, 1.8, 12) : 0;
        if (settle.HasValue)
        {
            CameraGeometry.Finite(settle.Value, "settle_delay_seconds");
            if (settle < 0 || settle > 15) throw new ArgumentException("settle_delay_seconds must be 0..15.");
            wait = Math.Max(wait, settle.Value);
        }
        if (wait > 0 && ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(wait))) ct.ThrowIfCancellationRequested();
        var after = ReadState();
        var readbackWait = Stopwatch.StartNew();
        bool Reached() => targets.All(p => after.Axes[p.Key].ReportedValue is int actual
            && Math.Abs((long)actual - p.Value) <= ((p.Key is "pan" or "tilt") ? 1 : Math.Max(1, after.Axes[p.Key].Step)));
        while (!Reached() && readbackWait.Elapsed < TimeSpan.FromSeconds(8))
        {
            if (ct.WaitHandle.WaitOne(150)) ct.ThrowIfCancellationRequested();
            after = ReadState();
        }
        if (!Reached()) throw new InvalidOperationException("Motor command was sent but readback did not reach the requested position; re-observe before another move.");
        return after;
    }

    public Task<CallToolResult> SetView(double? pan, double? tilt, double? zoom, CancellationToken ct, bool snapshot = false, double? settle = null, int maxWidth = 1280)
        => Run(snapshot ? "snapshot_after_move" : "set_view", token =>
        {
            ValidateCaptureWidth(maxWidth);
            if (settle.HasValue && (!double.IsFinite(settle.Value) || settle < 0 || settle > 15)) throw new ArgumentException("settle_delay_seconds must be 0..15.");
            var before = ReadState();
            var requested = new Dictionary<string, double>();
            if (pan.HasValue) requested["pan"] = pan.Value;
            if (tilt.HasValue) requested["tilt"] = tilt.Value;
            if (zoom.HasValue) requested["zoom"] = zoom.Value;
            var after = Move(before, requested, false, token, settle);
            return snapshot ? Capture(after, maxWidth, token) : Text(new { requested, state = after, settled_by = "estimated delay plus driver readback; not a motor-complete signal" });
        }, ct);

    public Task<CallToolResult> Nudge(double pan, double tilt, double zoom, bool zoomAware, CancellationToken ct)
        => Run("nudge", token =>
        {
            CameraGeometry.Finite(pan, "pan_delta_degrees"); CameraGeometry.Finite(tilt, "tilt_delta_degrees"); CameraGeometry.Finite(zoom, "zoom_delta_percent");
            if (Math.Abs(pan) > 30 || Math.Abs(tilt) > 30 || Math.Abs(zoom) > 25)
                throw new ArgumentException("One nudge is limited to +/-30 degrees per axis and +/-25 zoom percentage points.");
            var before = ReadState();
            var fov = Fov(before.ZoomPercent ?? 0);
            if (zoomAware) { pan *= fov.Horizontal / 72.5; tilt *= fov.Vertical / 44.8; }
            var targets = new Dictionary<string, double>();
            if (pan != 0) targets["pan"] = Writable(before, "pan").Value!.Value + pan;
            if (tilt != 0) targets["tilt"] = Writable(before, "tilt").Value!.Value + tilt;
            if (zoom != 0) targets["zoom"] = before.ZoomPercent!.Value + zoom;
            return Text(new { requested = targets, state = Move(before, targets, true, token), limits = "Requests clamp to driver limits; degree values round to effective hardware steps." });
        }, ct);

    public Task<CallToolResult> CenterOn(double x, double y, CancellationToken ct)
        => Run("center_on", token =>
        {
            // Validate coordinates before device access, including the no-op center point.
            CameraGeometry.CenterOffsets(x, y, 72.5, 44.8);
            var before = ReadState();
            var fov = Fov(before.ZoomPercent ?? 0);
            var delta = CameraGeometry.CenterOffsets(x, y, fov.Horizontal, fov.Vertical);
            var targets = new Dictionary<string, double>();
            if (delta.Pan != 0) targets["pan"] = Writable(before, "pan").Value!.Value + delta.Pan;
            if (delta.Tilt != 0) targets["tilt"] = Writable(before, "tilt").Value!.Value + delta.Tilt;
            return Text(new { image_x = x, image_y = y, pan_delta_degrees = delta.Pan, tilt_delta_degrees = delta.Tilt,
                calibration_estimated = calibration is null, state = Move(before, targets, true, token),
                guidance = "Use a fresh uncropped snapshot: x=-1 left/+1 right, y=-1 top/+1 bottom. Re-observe and refine after moving; limits and whole-degree resolution may prevent exact centering." });
        }, ct);

    public Task<CallToolResult> Home(CancellationToken ct, bool midpoint = false)
        => Run(midpoint ? "center" : "home", token =>
        {
            var before = ReadState(); var targets = new Dictionary<string, double>();
            foreach (string axis in new[] { "pan", "tilt" })
            {
                var p = Writable(before, axis);
                targets[axis] = midpoint ? p.Min + ((double)p.Max - p.Min) / 2 : p.DefaultValue;
            }
            if (!midpoint) targets["zoom"] = 0;
            return Text(new { requested = targets, state = Move(before, targets, true, token) });
        }, ct);

    public Task<CallToolResult> LegacyNudge(string axis, int steps, CancellationToken ct)
        => Run("legacy_nudge", token =>
        {
            if (axis is not ("pan" or "tilt" or "zoom") || steps < -5 || steps > 5)
                throw new ArgumentException("Use axis pan/tilt/zoom and steps -5..5.");
            var before = ReadState(); var p = Writable(before, axis);
            double native = (double)p.Value!.Value + (double)steps * p.Step;
            double target = axis == "zoom" ? CameraGeometry.ZoomPercent(CameraGeometry.Align(native, p.Min, p.Max, p.Step), p.Min, p.Max) : native;
            return Text(new { axis, steps, state = Move(before, new() { [axis] = target }, true, token) });
        }, ct);

    public Task<CallToolResult> Snapshot(int maxWidth, CancellationToken ct, bool wait = false, double timeout = 15)
        => Run(wait ? "observe" : "snapshot", token => { ValidateCaptureWidth(maxWidth); return Capture(ReadState(), maxWidth, token); }, ct, wait, timeout);

    public Task<CallToolResult> WaitUntilIdle(double timeout, CancellationToken ct)
        => Run("wait_until_idle", _ => Text(new { busy = false, state = ReadState() }), ct, true, timeout);

    private static void ValidateCaptureWidth(int width)
    {
        if (width < 320 || width > 1920) throw new ArgumentException("max_width must be 320..1920.");
    }

    private static CallToolResult Capture(CameraState state, int maxWidth, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var capture = new VideoCapture(state.DeviceIndex, VideoCaptureAPIs.DSHOW);
        if (!capture.IsOpened()) throw new InvalidOperationException($"Unable to open {state.Camera}; another camera application may own it.");
        capture.Set(VideoCaptureProperties.FrameWidth, 1920);
        capture.Set(VideoCaptureProperties.FrameHeight, 1080);
        using var frame = new Mat();
        int good = 0;
        for (int i = 0; i < 30 && good < 3; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (capture.Read(frame) && !frame.Empty()) good++;
            else if (ct.WaitHandle.WaitOne(80)) ct.ThrowIfCancellationRequested();
        }
        if (good < 3 || frame.Empty()) throw new InvalidOperationException("Camera opened but did not supply three fresh frames.");
        using var scaled = new Mat();
        Mat image = frame;
        if (frame.Width > maxWidth)
        {
            Cv2.Resize(frame, scaled, new OpenCvSharp.Size(maxWidth, Math.Max(1, (int)Math.Round((double)frame.Height * maxWidth / frame.Width))));
            image = scaled;
        }
        Cv2.ImEncode(".jpg", image, out byte[] jpg, new ImageEncodingParam(ImwriteFlags.JpegQuality, 82));
        if (jpg.Length == 0 || jpg.Length > 5_000_000)
            throw new InvalidOperationException("JPEG encoding failed or exceeded 5 MB.");
        return new CallToolResult { Content = [
            new TextContentBlock { Text = JsonSerializer.Serialize(new { camera = state.Camera, captured_at_utc = DateTimeOffset.UtcNow,
                width = image.Width, height = image.Height, bytes = jpg.Length, state, on_demand = true }, Json) },
            ImageContentBlock.FromBytes(jpg, "image/jpeg") ] };
    }

    public Task<CallToolResult> GetCalibration(CancellationToken ct) => Run("get_calibration", _ => Text(new
        { estimated = calibration is null, fov_table = calibration ?? [new(0, 72.5, 44.8), new(100, 6.9, 3.9)],
            interpolation = calibration is null ? "estimated linear focal length between manufacturer endpoints; not measured" : "piecewise linear FOV between measured points",
            coordinates = "uncropped image: x=-1 left/+1 right; y=-1 top/+1 bottom; center (0,0)" }), ct);

    public Task<CallToolResult> SetCalibration(CameraFovPoint[]? points, bool rollback, CancellationToken ct)
        => Run("set_calibration", _ =>
        {
            string backup = calibrationPath + ".previous";
            CameraFovPoint[]? updated;
            if (rollback)
            {
                if (points != null) throw new ArgumentException("Use either fov_table or rollback, not both.");
                if (!File.Exists(backup)) throw new ArgumentException("No previous calibration saved.");
                updated = JsonSerializer.Deserialize<CameraFovPoint[]>(File.ReadAllText(backup), Json);
                if (updated != null) ValidateCalibration(updated);
            }
            else updated = ValidateCalibration(points ?? throw new ArgumentException("Provide fov_table, or rollback=true."));
            Directory.CreateDirectory(Path.GetDirectoryName(calibrationPath)!);
            File.WriteAllText(backup, JsonSerializer.Serialize(calibration, Json));
            string temporary = calibrationPath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(updated, Json));
            File.Move(temporary, calibrationPath, true);
            calibration = updated;
            return Text(new { saved = true, estimated = calibration is null, fov_table = calibration });
        }, ct);

    private static CameraFovPoint[] ValidateCalibration(CameraFovPoint[] points)
    {
        if (points is null || points.Length < 2 || points.Length > 30) throw new ArgumentException("Calibration needs 2..30 points, ordered from zoom_percent 0 to 100.");
        if (points[0] is null || points[^1] is null || points[0].ZoomPercent != 0 || points[^1].ZoomPercent != 100) throw new ArgumentException("Calibration must include zoom_percent 0 and 100.");
        for (int i = 0; i < points.Length; i++)
        {
            var p = points[i] ?? throw new ArgumentException("Calibration points must not be null.");
            CameraGeometry.CenterOffsets(0, 0, p.HorizontalFovDegrees, p.VerticalFovDegrees);
            CameraGeometry.Finite(p.ZoomPercent, "zoom_percent");
            if (p.ZoomPercent < 0 || p.ZoomPercent > 100 || (i > 0 && (p.ZoomPercent <= points[i-1].ZoomPercent || p.HorizontalFovDegrees > points[i-1].HorizontalFovDegrees || p.VerticalFovDegrees > points[i-1].VerticalFovDegrees)))
                throw new ArgumentException("Zoom must increase strictly; FOV must not increase as zoom increases.");
        }
        return points;
    }
}
