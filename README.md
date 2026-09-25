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

`SliceTranscribe.exe`

Button-driven microphone recorder and Hungarian transcription app for the
Slice. It references `SliceControl.dll` directly and uses NAudio for Windows
WASAPI microphone capture. The default transcription backend is now the
whisper.cpp server on the RTX 3090 machine; local CPU-only Whisper and the
OpenAI realtime backend remain available as fallbacks.

SliceControl itself has no external NuGet dependencies; SliceTranscribe uses
NAudio.

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
slice.Telephony.ApplyMuteTheme(
    SliceTelephonyState.ActiveImmediateExit);
```

`42 01` latches the muted theme, but hardware testing showed that an already
running active renderer may continue showing green until the lifecycle state
changes. For an immediate visible red/yellow transition, use
`ApplyMuteTheme(...)`, which briefly leaves the active renderer, applies
`42 01`, then resumes the requested active state with short firmware-settle
delays.

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

## SliceTranscribe

SliceTranscribe now combines the validated Collaboration Cover -> microphone ->
WAV lifecycle with optional live Hungarian speech-to-text.

Build everything:

```powershell
git pull
dotnet build SliceControl.slnx
```

List active microphone endpoints:

```powershell
.\src\SliceTranscribe\bin\Debug\net8.0-windows\SliceTranscribe.exe mics
```

Run using the default communications microphone:

```powershell
.\src\SliceTranscribe\bin\Debug\net8.0-windows\SliceTranscribe.exe run
```

Or select a microphone by a unique part of its name:

```powershell
.\src\SliceTranscribe\bin\Debug\net8.0-windows\SliceTranscribe.exe run --mic "Bang & Olufsen"
```

The current controls are:

```text
GREEN pickup  -> start recording
MUTE          -> pause / resume recording
RED hangup    -> stop and finalize WAV
VOL +/-       -> detected, reserved for app controls
Ctrl+C        -> quit safely
```

While recording, the panel enters the green active-call presentation. Pausing
switches to the red/yellow muted presentation; resuming returns to green.
SliceTranscribe now drives these visuals directly through the proprietary
Collection 03 `FE` protocol instead of the stock Collection 01 telephony state
machine. This avoids the driver's latched `42 01/00` mute-theme behavior that
could make pause/resume appear visually inverted. Stopping uses the direct exit
animation and reset.

Recordings default to:

```text
%USERPROFILE%\Documents\SliceTranscribe\Recordings
```

A custom output directory can be supplied with:

```powershell
.\src\SliceTranscribe\bin\Debug\net8.0-windows\SliceTranscribe.exe run --output "D:\Recordings"
```

SliceTranscribe temporarily stops `HPSliceTelephonyService` while running so
it can own the private button-event registration. It restores the service on
normal exit and through its cleanup path after application errors.

### Six-channel microphone probe

The HP Bang & Olufsen capture endpoint exposes six channels. To diagnose which
array channel best isolates useful speech before choosing a fixed channel, run
the same recording through large-v3 one channel at a time:

```powershell
.\src\SliceTranscribe\bin\Debug\net8.0-windows\SliceTranscribe.exe probechannels "C:\path\recording.wav"
```

The command sends each extracted 16 kHz mono channel to the configured remote
whisper.cpp server and prints `CHANNEL 0` through `CHANNEL 5` transcripts.
This is preferable to guessing from aggregate energy because the loudest
microphone channel may simply be the one closest to unrelated background
conversation.

### Remote RTX 3090 transcription

The default backend is now the whisper.cpp HTTP server at:

```text
http://192.168.1.2:8765
```

This is intended to keep `large-v3` resident on the RTX 3090 while the
Slice remains responsible only for microphone capture, button handling, WAV
recording, chunking, and HTTP transport.

Run normally:

```powershell
.\src\SliceTranscribe\bin\Debug\net8.0-windows\SliceTranscribe.exe run --mic "HP Bang & Olufsen Audio Module"
```

The same mode can be selected explicitly:

```powershell
SliceTranscribe run --transcriber remote --remote-url "http://192.168.1.2:8765"
```

Roughly twelve-second microphone chunks are taken from a fixed B&O input
channel, converted to 16 kHz mono PCM16 WAV, filtered through the same light
client-side speech-energy gate used by the local backend, then POSTed to
whisper.cpp's `/inference` endpoint. Channel 0 is the current default and can
be overridden with `--remote-channel`. Each
request explicitly asks for Hungarian, JSON output, zero temperature,
non-speech suppression, and the shop-specific prompt.

Completed chunks are printed as:

```text
REMOTE TEXT -> ...
```

and appended to the matching UTF-8 transcript beside the source WAV.

The remote backend checks the server UI before starting transcription. If the
server cannot be reached, WAV recording continues and the app reports the
transcription startup failure.

### Local Hungarian transcription

Local Whisper remains available as a fallback backend. SliceTranscribe uses
the multilingual Whisper `base` model on the CPU and forces language `hu`.

The model is downloaded only once and stored outside the repository:

```text
%LOCALAPPDATA%\SliceTranscribe\Models\ggml-base.bin
```

You can download it before the first recording:

```powershell
.\src\SliceTranscribe\bin\Debug\net8.0-windows\SliceTranscribe.exe model
```

Then run normally:

```powershell
.\src\SliceTranscribe\bin\Debug\net8.0-windows\SliceTranscribe.exe run --mic "HP Bang & Olufsen Audio Module"
```

The app records the original microphone stream to WAV while a background
worker converts roughly six-second chunks to 16 kHz mono and transcribes them
with Whisper. Completed chunks are printed as `LOCAL TEXT -> ...` and
appended to a UTF-8 text file beside the WAV. Before invoking Whisper, a small
20 ms frame-based energy gate rejects silence/noise-only chunks; the Whisper
processor also runs with no cross-chunk context, zero temperature, and a more
aggressive no-speech threshold to reduce repeated silence hallucinations.

```text
slice-20260925-123456.wav
slice-20260925-123456.txt
```

Mute immediately pauses the WAV/transcription feed and asks the local worker
to flush the current partial chunk. Hangup stops the microphone and waits for
any queued CPU transcription work before finalizing the TXT.

Whisper.net is intentionally pinned to `1.8.1` for this Windows 10 Slice.
Its CPU runtime supports Windows with the Visual C++ 2019-or-newer runtime;
the newer Whisper.net 1.9.x CPU runtime currently documents Windows 11 /
Windows Server 2022 as its Windows minimum.

The default model can be overridden:

```powershell
SliceTranscribe run --model "D:\Models\ggml-base.bin"
```

The previous OpenAI realtime backend is still available explicitly:

```powershell
$env:OPENAI_API_KEY = "your-api-key"
SliceTranscribe run --transcriber openai
```

Disable transcription entirely with:

```powershell
SliceTranscribe run --transcriber none
```

or the shorthand `--no-transcribe`.


## Status

This is an experimental reverse-engineering project.

See `protocol/README.md` for confirmed HID reports, observed LED behavior, and protocol notes.
