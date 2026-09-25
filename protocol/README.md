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

The descriptor contains both absolute and relative controls, so not every bit
should be interpreted as a held key.

Current API interpretation:

- Absolute/state-like controls are surfaced as Down/Up transitions.
- Relative controls such as Phone Mute are surfaced as Triggered events.
- Raw `32 00` reports are retained for reverse engineering and are not assumed
  to mean a dedicated physical Hangup press.

Observed during testing:

```text
32 02
32 00

32 10

32 00
```

Current confirmed physical behavior:

- `32 02` is produced by the green pickup control in the tested sequence.
- `32 10` is associated with the mute control and behaves like a triggered
  telephony action rather than a normal held key.
- The red hangup control does not currently expose a unique, confirmed bit in
  the decoded Col01 stream. It may clear telephony state or use another
  collection.

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

Observed state behavior:

| Value | Observed behavior |
|---:|---|
| `00` | Idle/end-call request |
| `02` | Green call entrance animation, then steady call indicators |
| `04` | Green blinking/attention state; pickup and hangup indicators blink |
| `20` | Green breathing active-call state; ending from this state is delayed by about 5 seconds |
| `22` | Green breathing active-call state; visually similar to `20`, but ending exits immediately |

Important observations:

- `20` and `22` look essentially the same while active, but their exit timing differs.
- Switching `20 -> 22` or `22 -> 20` can be visually silent; the difference becomes visible when ending the call.
- `20 -> 04 -> 20` produces breathing -> green blinking -> breathing.
- `22 -> 04 -> 22` produces breathing -> green blinking -> breathing, then an immediate exit on `00`.
- `04` from idle enters the blinking attention state directly without an entrance animation.
- `00` from `20` allows roughly two more breathing cycles before the exit animation.
- `00` from `22` starts the exit animation immediately.

These reports are handled by the HP driver as lifecycle/state requests rather
than direct one-bit LED controls.

### Report `0x42`

Report layout:

```text
42 01
```

Observed behavior:

- `42 01` switches an active call presentation to the red/muted theme.
- In the muted theme, the perimeter breathes red and the yellow mute indicator is illuminated.
- Entering the `41 04` attention state temporarily overrides the muted presentation with green blinking.
- Returning from attention to the prior active state restores the red/muted presentation, demonstrating that the mute/theme state remains latched underneath the attention renderer.
- `42 00` did not visibly restore the normal green theme in testing, even when followed by an attention-state round trip. It is therefore exposed by SliceControl as an experimental/raw-compatible operation rather than documented as a guaranteed visual unmute.

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

# High-level HP Telephony State Machine

The stock HP driver translates Collection 01 reports into the proprietary
Collection 03 `FE` command family.

Observed mapping:

| Collection 01 state | Low-level behavior |
|---|---|
| `41 02` | Green entrance sequence using `FE 04 02` followed by `FE 01 02` |
| `41 20` | Green breathing active state using the `FE 02` family; delayed exit |
| `41 22` | Green breathing active state using the `FE 02` family; immediate exit |
| `41 04` | Green blinking/attention state using the `FE 03` family |
| `41 00` | Return toward idle/end state |

Mute/theme behavior is controlled separately by report `42`.

This separation is important: lifecycle state and visual mute theme are not
the same thing. During a muted active call, entering `41 04` produces green
blinking, then returning to the active lifecycle state restores the red/yellow
muted presentation.

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

## Attention / Ringing State

```text
FE 00 03 00 00 00 00 00
```

Observed behavior:

- Green perimeter flashes continuously.
- Pickup and hangup indicators participate in the blinking attention presentation.
- This renderer is green even when the underlying active call is in the red/yellow muted theme.
- Returning to the prior active state restores the muted theme if it was active before the attention state.
- Animation continues until another lifecycle state or reset is sent.

---


## Active Breathing State

General form:

```text
FE 00 02 MODE 00 00 00 00
```

Observed modes:

### Mode `00`

```text
FE 00 02 00 00 00 00 00
```

No visible effect was observed.

### Mode `02` - Normal active call

```text
FE 00 02 02 00 00 00 00
```

Observed behavior:

- Green perimeter breathes on and off.
- Green pickup indicator participates in the breathing presentation.
- Red hangup indicator remains steadily illuminated.

### Mode `03` - Muted active call

```text
FE 00 02 03 00 00 00 00
```

Observed behavior:

- Red perimeter breathes on and off.
- Red active-call presentation is used.
- Red hangup indicator remains steadily illuminated.
- Yellow mute indicator remains steadily illuminated.

---

## Call Entrance Sequence

The stock driver uses a two-command sequence:

```text
FE 00 04 MODE 00 00 00 00
FE 00 01 MODE 00 00 00 00
```

with a very short delay between the commands.

For mode `02`:

- Green light starts at the front center.
- It spreads around both sides toward the back.
- Green pickup and red hangup indicators illuminate.

For mode `03`:

- The same entrance geometry is used with the red/muted theme.
- Yellow mute is illuminated.

Testing `FE 00 04 02 ...` by itself produced the green entrance/wraparound
effect and call indicators. Testing `FE 00 01 02 ...` from reset caused the
LEDs to blink off briefly and then return, showing that these commands are
stateful and are best treated as a sequence.

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

- Green wraparound/unwrap transition.
- LEDs disappear back-to-front.
- Final reset returns the panel to idle.

Observed when exiting a red/muted call state:

- A brief red blink occurs first.
- The red perimeter then unwraps/disappears.
- Indicators turn off correctly at the end.

The color of the exit animation inherits the currently active call theme.

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

SliceControl now discovers this collection and includes it in `watchraw`
when present.

During direct button testing, Collection 05 remained silent for the tested
pickup, hangup, mute, volume-down, and volume-up sequence.

The red hangup control therefore does not appear to use Collection 05 in the
tested configuration. In Collection 01 traces it is associated with an
otherwise-ambiguous `32 00` state-clear report rather than a unique dedicated
button bit.

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
- Collection 05 reverse engineering, including the red hangup control.
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