# HP Elite Slice G2 Collaboration Cover Protocol

Reverse-engineering notes for the HP Elite Slice G2 Collaboration Cover.

This information was determined experimentally using the original HP driver,
Windows HID enumeration, HID report descriptors, `hidapitester`, and direct
testing on real hardware.

## Hardware

Tested device:

- HP Elite Slice G2
- HP Collaboration Cover
- USB VID: `03F0`
- USB PID: `0D66`
- HP telephony driver: `1.0.1.123`
- Windows 10 22H2

The device exposes multiple HID collections.

---

# HID Collections

## Collection 01 - Telephony

Device path contains:

```text
VID_03F0&PID_0D66&Col01
```

Usage page:

```text
0x000B - Telephony
```

Input report ID:

```text
0x32
```

The report consists of:

```text
32 XX
```

where `XX` is a one-byte bitfield.

Descriptor-derived mapping:

| Mask | HID meaning |
|---:|---|
| `01` | Hook Switch |
| `02` | Flash |
| `04` | Redial |
| `08` | Speaker Phone |
| `10` | Phone Mute |
| `20` | Send |
| `40` | Speed Dial |
| `80` | Button 7 |

Release is represented by the corresponding bit clearing.

For example:

```text
32 10
```

means the Phone Mute control is pressed.

```text
32 00
```

means all controls are released.

Observed during testing:

```text
32 02
32 00

32 10
32 00
```

The exact physical-button mapping is still being documented.

---

## Collection 01 - Telephony LED output reports

The Telephony collection also exposes LED output reports.

### Report `0x41`

Report layout:

```text
41 XX
```

`XX` is a seven-bit telephony LED state field.

Tentative observed behavior during direct testing:

| Value | Observed behavior |
|---:|---|
| `01` | No obvious visible effect |
| `02` | Green fill/sweep in one direction |
| `04` | Green fill/sweep in opposite direction |
| `08` | No obvious visible effect |
| `10` | Green flashing |
| `20` | No obvious visible effect |
| `40` | Slow green pulsing |

These effects appear to be firmware-stateful rather than simple LED on/off
bits.

### Report `0x42`

Report layout:

```text
42 01
```

This corresponds to a standard mute LED output report.

No obvious visible effect was observed during direct testing.

---

# Collection 02 - Consumer Control

Device path contains:

```text
VID_03F0&PID_0D66&Col02
```

Usage page:

```text
0x000C - Consumer Control
```

Input report ID:

```text
0x31
```

Confirmed mapping:

| Report | Meaning |
|---|---|
| `31 01` | Volume Up pressed |
| `31 02` | Volume Down pressed |
| `31 00` | Released |

These mappings were confirmed experimentally.

Example sequence:

```text
31 02
31 00
31 02
31 00
31 01
31 00
```

The normal Windows HID stack also receives these controls, so Volume Up and
Volume Down currently continue to change the Windows master volume while they
are being monitored by user-mode software.

---

# Collection 03 - HP Vendor Protocol

Device path contains:

```text
VID_03F0&PID_0D66&Col03
```

Usage page:

```text
0xFF00
```

Report descriptor:

```text
06 00 FF
09 01
A1 01
85 FE
19 01
29 01
16 80 00
25 7F
75 08
95 07
91 02
C0
```

Output report ID:

```text
0xFE
```

Payload length:

```text
7 bytes
```

Total report length including report ID:

```text
8 bytes
```

General packet structure discovered so far:

```text
FE 00 COMMAND ARG1 ARG2 00 00 00
```

---

# Confirmed Collection 03 Commands

## Hard Reset / Idle

```text
FE 00 00 00 00 00 00 00
```

Observed behavior:

- Immediately stops active LED animations.
- Returns the panel to idle state.

This is currently the most reliable "stop whatever the LEDs are doing"
command.

---

## Incoming Call / Ringing State

```text
FE 00 03 00 00 00 00 00
```

Observed behavior:

- Green ring flashes continuously.
- Green pickup icon illuminated.
- Red hangup icon illuminated.
- Animation continues indefinitely until another state or reset is sent.

---

## State Exit Transition

```text
FE 00 05 00 00 00 00 00
```

This command appears to initiate an exit animation based on the current
firmware state.

It is normally followed by:

```text
FE 00 00 00 00 00 00 00
```

Observed when exiting a green call state:

- Green wraparound transition.
- LEDs then disappear back-to-front.
- Final reset returns the panel to idle.

The color and animation produced by command `05` appear to depend on the state
that was active before it was issued.

---

# Level / Status Command

Command `07` accepts a mode byte and a value byte.

General structure:

```text
FE 00 07 MODE VALUE 00 00 00
```

`VALUE` is decimal `0` through `100`.

Hexadecimal examples:

```text
0   = 00
25  = 19
50  = 32
75  = 4B
100 = 64
```

---

## Mode `00` - White Level Bar

```text
FE 00 07 00 VV 00 00 00
```

Example, 50 percent:

```text
FE 00 07 00 32 00 00 00
```

Example, 100 percent:

```text
FE 00 07 00 64 00 00 00
```

Observed behavior:

- Displays a white segmented bar.
- Level tracks values from 0 through 100.
- Updating the value rapidly allows smooth custom sweep animations.

At value zero, a very small amount of illumination may remain visible.

---

## Mode `01`

```text
FE 00 07 01 VV 00 00 00
```

No visible effect was observed during testing.

---

## Mode `02` - Active Call

```text
FE 00 07 02 VV 00 00 00
```

Observed behavior:

- White level bar.
- Green pickup icon.
- Red hangup icon.

Example:

```text
FE 00 07 02 32 00 00 00
```

displays approximately 50 percent bar level with active-call icons.

---

## Mode `03` - Active Muted Call

```text
FE 00 07 03 VV 00 00 00
```

Observed behavior:

- White level bar.
- Green pickup icon.
- Red hangup icon.
- Yellow mute icon.

Example:

```text
FE 00 07 03 32 00 00 00
```

---

# Hello Animation

```text
FE 00 08 00 00 00 00 00
```

Observed behavior:

- White illumination sweeps around the panel in one direction.
- Appears to behave like a firmware "hello" or startup transition.

The animation is stateful.

Sending command `08` repeatedly without changing state does not necessarily
replay the animation.

---

# Goodbye Animation

```text
FE 00 09 00 00 00 00 00
```

Observed behavior:

- White illumination sweeps around the panel in the opposite direction.
- Appears to behave like a firmware shutdown/goodbye transition.

Changing state between `08` and `09` allows the animations to be replayed.

---

# Custom Bar Animation

Because command `07` accepts arbitrary values from 0 through 100, applications
can generate their own animations by rapidly updating the level.

Conceptually:

```text
0
1
2
3
...
100
99
98
...
0
```

This produces a smooth white fill and drain animation.

---

# Collection 04 - Unknown Vendor Control

Device path contains:

```text
VID_03F0&PID_0D66&Col04
```

Usage page:

```text
0xFF00
```

Report descriptor:

```text
06 00 FF
09 01
A1 01
85 FF
19 01
29 01
16 80 00
25 7F
75 08
95 01
91 02
C0
```

Output report ID:

```text
0xFF
```

Payload:

```text
1 byte
```

Total report:

```text
FF XX
```

The purpose of this collection is currently unknown.

SliceControl intentionally exposes raw access to this collection for future
reverse engineering.

---

# Collection 05 - Keyboard

Device path contains:

```text
VID_03F0&PID_0D66&Col05
```

Usage page:

```text
0x0001 - Generic Desktop
```

Usage:

```text
0x0006 - Keyboard
```

Windows exposes this as a standard HID keyboard device.

Further investigation is still required to determine which Collaboration
Cover actions are emitted through this collection.

---

# Stock HP Driver

Known working HP driver:

```text
HID Telephony for Slice
Version 1.0.1.123
```

The package installs:

```text
HPSliceTelephony.sys
HPSliceTelephonyService.exe
```

Service name:

```text
HPSliceTelephonyService
```

The service starts automatically by default.

It reacts to Windows audio and telephony state and can overwrite custom LED
states.

For exclusive direct control, stop it:

```powershell
Stop-Service HPSliceTelephonyService -Force
```

Restore HP behavior with:

```powershell
Start-Service HPSliceTelephonyService
```

SliceControl also exposes CLI commands for this:

```text
slicectl service stop
slicectl service start
slicectl service query
```

Administrator privileges are normally required to control the service.

---

# Reverse Engineering Tools Used

The protocol has been investigated using:

- Windows PowerShell
- Windows PnP APIs
- HID report descriptors
- `hidapitester`
- the original HP driver package
- direct hardware observation

Useful `hidapitester` device:

```text
VID 03F0
PID 0D66
```

Example Collection 03 report:

```text
FE 00 07 00 32 00 00 00
```

---

# Current SliceControl Goals

The SliceControl project is intended to provide:

- Automatic device discovery.
- No machine-specific HID paths.
- Direct LED control.
- Button input monitoring.
- Raw HID access for continued reverse engineering.
- A reusable .NET API.
- A command-line testing utility.
- Optional ownership of the HP telephony service.

Future work may include:

- Definitive physical-button mapping.
- Collection 04 reverse engineering.
- Collection 05 reverse engineering.
- Additional undocumented LED states.
- Event-based application bindings.
- A Windows HID filter driver capable of intercepting selected controls
  before Windows consumes them.
- A test-signed KMDF driver for full programmable-button ownership.

---

# Safety / Compatibility

This protocol is undocumented by HP and was reverse engineered experimentally.

Behavior may differ across:

- Collaboration Cover firmware revisions.
- Elite Slice variants.
- HP driver versions.
- Windows versions.

Raw commands should therefore be treated as experimental.

Known-safe commands are documented separately from unknown/raw commands.