# EagleEye USB camera tools

The Slice MCP endpoint remains `http://10.10.10.12:8790/mcp`. Camera tools use
the **Polycom EagleEye IV USB Camera attached to the Windows Slice**. They do
not call the old Raspberry Pi camera server or rely on its multicast stream.

## Main tools

| Tool | Purpose |
| --- | --- |
| `camera_get_state()` | Fresh pan/tilt degrees, zoom position percent, autofocus, FOV and native ranges. |
| `camera_status()` | State, busy/active operation, last error and server uptime. While busy, returns cached state without interfering. |
| `camera_set_view(pan_degrees, tilt_degrees, zoom_percent?)` | Absolute PTZ; unspecified zoom stays unchanged. |
| `camera_nudge(pan_delta_degrees=0, tilt_delta_degrees=0, zoom_delta_percent=0, zoom_aware=false)` | Relative motion, bounded to 30 degrees per angular axis and 25 zoom percentage points per call. |
| `camera_center_on(image_x, image_y)` | Point the camera at a target identified in a fresh snapshot. |
| `camera_set_zoom(zoom_percent)` | Set position within the native zoom range, preserving pan/tilt. |
| `camera_home()` | Driver neutral/default pan and tilt, fully zoomed out. |
| `camera_snapshot(max_width=1280)` | Fresh JPEG as native MCP image content, with capture time, dimensions and PTZ metadata. |
| `camera_observe(timeout_seconds=15, max_width=1280)` | Wait for idle, then get state and a snapshot under one lock. |
| `camera_snapshot_after_move(pan_degrees?, tilt_degrees?, zoom_percent?, settle_delay_seconds?, max_width=1280)` | Move optional absolute axes, wait and take a snapshot atomically. |
| `camera_wait_until_idle(timeout_seconds=15)` | Wait for the current operation and settle interval to finish. |
| `camera_get_calibration()` | Show the FOV model/table and coordinate conventions. |
| `camera_set_calibration(fov_table?, rollback=false)` | Save a measured FOV table, or restore the previous table. |

The existing `eagleeye_status`, `eagleeye_nudge(axis, steps)`,
`eagleeye_center` and `eagleeye_snapshot` remain available. The legacy center
tool retains its midpoint-of-range behavior. Use `camera_home` for neutral
position or `camera_center_on` to center an image target.

## Center on something in a picture

1. Call `camera_observe` and inspect its image.
2. Choose the target's pixel center `(px, py)` in that **uncropped image**.
3. Convert to normalized coordinates:
   `image_x = 2 * px / width - 1`, `image_y = 2 * py / height - 1`.
4. Call `camera_center_on(image_x, image_y)`.
5. Call `camera_observe` again and refine if necessary.

`(-1,-1)` is the top-left, `(0,0)` the center and `(+1,+1)` the bottom-right.
Positive image x moves the camera right; positive image y tilts it down.
Perspective geometry uses the current zoom/FOV; offsets aren't simply
multiplied by half the field of view. This tool moves to a supplied target;
it does not itself identify objects or continuously track people.

The initial FOV model uses the EagleEye **IV USB** datasheet endpoints:
72.5/44.8 degrees horizontal/vertical at wide angle and 6.9/3.9 at telephoto.
Intermediate zoom uses an estimated focal-length model. Responses explicitly
mark this `estimated: true`; it is not an empirical calibration. A visual
observe/center/observe loop is therefore preferable to assuming exact arrival.

For a measured table, pass 2..30 ordered points with fields `zoom_percent`,
`horizontal_fov_degrees`, `vertical_fov_degrees`. Include zoom 0 and 100;
zoom must increase strictly and FOV cannot increase with zoom. Saved points
use piecewise linear FOV interpolation and live outside the checkout at
`%LOCALAPPDATA%\SliceAppliance\eagleeye-fov.json`. Each write saves a previous
table; `rollback=true` restores it, including the initial estimated model.

## Driver behavior and coordination

This Slice reports pan `-170..170`, tilt `-30..90`, zoom `0..5680` and
autofocus support. Pan/tilt use degrees. Zoom percent is position within the
reported range, not a measured optical magnification ratio.

The camera reports a zero pan/tilt step. Those axes therefore use a documented
one-degree fallback, rather than being rejected as unsupported. Native
readback can truncate a command by one degree. Axis responses include
`reported_value`, the effective `value`, `value_source` and
`uncertainty_degrees`. A known command is retained only when it is within one
degree of native readback; larger differences remain visible as native state.

One persistent STA control session owns the DirectShow COM interface. Repeatedly
recreating it between video captures can reset the driver's reported PTZ state
without matching the physical position. Control, capture, settlement and
calibration share one gate. New moves fail with `camera_busy` instead of being
queued; observe/idle calls may wait up to 30 seconds. Invalid multi-axis targets
are validated before sending any motor command. Absolute moves reject invalid
ranges; relative/target-centering moves clamp to driver stops.

Settlement combines a conservative delay and bounded position readback polling.
Driver readback is not a physical motor-completion signal. If a target is not
reached within the readback deadline, the tool returns an error and asks for
re-observation. Request cancellation is cooperative; native capture already in
progress must return before the shared gate is released.

Snapshots open a video capture only for the requested operation, discard initial
frames, encode one JPEG and release video capture. There is no background image
recording or photograph persistence on the server. `max_width` accepts 320..1920;
the image is never upscaled and its actual dimensions are included. Payloads
are bounded to 5 MB and returned as a native `image/jpeg` MCP block, not a file
path or a base64 string hidden inside text.

This release implements the core motion/vision workflow, not the old server's
presets, emotes, multicast administration, continuous tracking or UVC stop API.
Existing trusted-network MCP access rules still apply.

## Build, test and deploy

```powershell
dotnet run --project tests\SliceMcp.Camera.Tests\Camera.Tests.csproj -c Release
dotnet publish src\SliceMcp\SliceMcp.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:PublishTrimmed=false -o "$env:LOCALAPPDATA\SliceAppliance\CameraStage"
```

The dependency-free console checks cover bounds, the actual zero-step driver
quirk, normalization, finite input validation, FOV endpoints and perspective
coordinate conversion. Real-device acceptance also checks state preservation
across snapshots, relative movement, zoom, image-target centering, invalid
input without movement, busy exclusion and actual JPEG image blocks over MCP.

For an isolated test process, set `Mcp__Port=18790` and
`Mcp__BindAddress=127.0.0.1` in that process's environment. Production defaults
remain unchanged. After validating a staged publish, stop only the `Slice MCP`
scheduled task/process, back up its publish directory, replace that directory's
files and restart the task. The radio and transcription processes do not need
to restart. Reconnect the MCP client to refresh its tool catalogue.

Native interface definitions: [Microsoft Windows SDK strmif.h](https://github.com/microsoft/win32metadata/blob/main/generation/WinSDK/RecompiledIdlHeaders/um/strmif.h).
Pan/tilt units: [Microsoft CameraControlProperty](https://learn.microsoft.com/en-us/windows/win32/api/strmif/ne-strmif-cameracontrolproperty).
USB model endpoints: [Polycom EagleEye IV USB datasheet](https://polyvietnam.vn/wp-content/uploads/2024/07/Poly_EagleEye_IV_USB_Datasheet.pdf).
