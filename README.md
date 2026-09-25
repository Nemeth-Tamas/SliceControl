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


## Telephony state API

SliceControl now exposes the higher-level HP telephony state reports in
addition to the direct low-level `FE` LED protocol.

Example:

```csharp
using var slice = SliceDevice.Open();

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

`SendMuteOffReport()` sends `42 00`, but on the tested hardware this did not
visibly restore the normal green presentation, so it should currently be
treated as experimental behavior rather than a guaranteed visual unmute.

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
