# SliceControl

Unofficial control library and CLI for the HP Elite Slice G2 Collaboration Cover.

This project exists because the HP Elite Slice G2 has a surprisingly capable capacitive communications panel with programmable LEDs and HID buttons, and it deserves a second life.

## Hardware

Currently tested with:

- HP Elite Slice G2
- HP Collaboration Cover
- USB VID `03F0`
- USB PID `0D66`
- HP telephony driver `1.0.1.123`
- Windows 10 22H2

## Components

`SliceControl.dll`

Reusable .NET API.

`slicectl.exe`

Small command-line utility built on the same API.

No external NuGet packages are currently required.

## Build

Install the .NET 8 SDK or newer.

From the repository root:

```powershell
dotnet build SliceControl.slnx
```

Release build:

```powershell
dotnet publish .\src\SliceCtl\SliceCtl.csproj -c Release -r win-x64 --self-contained false
```

## Discover the device

```powershell
slicectl devices
```

The program discovers the Slice dynamically by VID/PID and collection:

```text
VID_03F0
PID_0D66
COL01
COL02
COL03
COL04
COL05
```

No machine-specific HID path is hardcoded.

## LED examples

Plain white bar:

```powershell
slicectl bar 75
```

Call state:

```powershell
slicectl call 50
```

Muted call state:

```powershell
slicectl muted 50
```

Ringing:

```powershell
slicectl ring
```

Hard reset:

```powershell
slicectl off
```

Hello animation:

```powershell
slicectl hello
```

Goodbye animation:

```powershell
slicectl goodbye
```

Smooth test sweep:

```powershell
slicectl sweep
```

## Button monitor

```powershell
slicectl watch
```

Example output:

```text
09:34:49.541  VolumeDown Down [report=0x31, value=0x02]
09:34:49.692  VolumeDown Up [report=0x31, value=0x00]
09:35:46.877  PhoneMute Triggered [report=0x32, value=0x10]
```

Relative telephony controls such as Phone Mute are exposed as `Triggered` events rather than fake Down/Up pairs.

## Physical Collaboration Cover buttons

For application code, prefer the physical-button API when you care about the
actual controls printed on the cover rather than the descriptor-level HID
names:

```powershell
slicectl watchbuttons
```

The CLI temporarily stops `HPSliceTelephonyService` while it owns the HP
private key-event registration, then restores the service when monitoring
stops.

Example output:

```text
11:42:18.112  Pickup      COL01 IN  32 02 | COL01 IN  32 00
11:42:19.540  Hangup      COL01 IN  32 00
11:42:20.873  Mute        COL01 IN  32 00 | COL01 IN  32 10
11:42:22.101  VolumeDown  COL02 IN  31 02
11:42:23.447  VolumeUp    COL02 IN  31 01
```

Reusable API:

```csharp
SliceDevice slice = SliceDevice.Open();

await slice.WatchPhysicalButtonsAsync(
    ev =>
    {
        Console.WriteLine(ev.Button);
    },
    cancellationToken);
```

Known physical values are `Pickup`, `Hangup`, `Mute`, `VolumeUp`, and
`VolumeDown`. The monitor combines the HP driver's private recognized-key
event with the translated Collection 01/02 reports. This is specifically what
allows a standalone `32 00` to be identified as the physical red Hangup
button instead of treating every zero-state report as Hangup.

## Private HP driver API

The stock driver exposes the private device
`\\.\HPSlicePDO_SYM_03F0`. SliceControl wraps it as a persistent,
disposable session:

```csharp
using SlicePrivateDriver hp = slice.OpenPrivateDriver();

hp.Hello();
hp.SetVolume(50);
hp.Goodbye();

await hp.WaitForKeyPressAsync(cancellationToken);
```

The event registration and volume notification are stateful per open handle,
so the same `SlicePrivateDriver` instance must be kept alive.

## Telephony state API

SliceControl now exposes the higher-level HP telephony state reports in
addition to the direct low-level `FE` LED protocol.

Example:

```csharp
SliceDevice slice = SliceDevice.Open();

slice.Telephony.EnterCall();
slice.Telephony.ActiveDelayedExit();
slice.Telephony.Attention();
slice.Telephony.EndCall();
```

Known high-level lifecycle states:

| API | Report | Observed behavior |
|---|---|---|
| `EnterCall()` | `41 02` | Green entrance animation |
| `Attention()` | `41 04` | Green blinking attention/ringing state |
| `ActiveDelayedExit()` | `41 20` | Green breathing; end request waits about 5 seconds |
| `ActiveImmediateExit()` | `41 22` | Green breathing; end request exits immediately |
| `EndCall()` | `41 00` | Return toward idle/end state |

Mute/theme control is separate:

```csharp
slice.Telephony.EnableMuteTheme();
```

`42 01` switches an active presentation into the observed red breathing /
yellow mute theme. The attention state temporarily renders green, then the
red/muted presentation returns when the active state is restored.

`SendMuteOffReport()` sends the raw `42 00` report. While an active
`41 20` / `41 22` state is being rendered, that report alone does not visibly
clear the latched red/muted theme.

The confirmed unmute sequence is:

```csharp
slice.Telephony.ClearMuteTheme(
    SliceTelephonyState.ActiveImmediateExit);
```

Internally this performs:

```text
41 02
42 00
41 22
```

A single `42 00` is sufficient once the driver has first been moved out of the
active breathing state. The earlier two-zero hypothesis was disproved by
hardware testing.

Raw state reports remain available:

```csharp
slice.Telephony.SendStateReport(0x22);
slice.Telephony.SendMuteReport(0x01);
```

## Raw input monitor

```powershell
slicectl watchraw
```

This prints raw reports from known input collections. Current raw monitoring covers:

- Collection 01 - Telephony
- Collection 02 - Consumer Control
- Collection 05 - Keyboard, when present

This is intended for reverse engineering and for validating the interpreted button API.

## Raw reverse-engineering access

Collection 03:

```powershell
slicectl raw FE 00 07 00 32 00 00 00
```

Collection 04:

```powershell
slicectl rawff 00
```

Raw commands are intentionally exposed so new protocol behavior can be researched without changing the library.

## HP service ownership

The stock service may overwrite manually selected LED states.

Stop it:

```powershell
slicectl service stop
```

Restore it:

```powershell
slicectl service start
```

Stopping/starting the service normally requires an elevated terminal.

## Status

This is an experimental reverse-engineering project.

See `protocol/README.md` for confirmed HID reports, observed LED behavior, and protocol notes.
