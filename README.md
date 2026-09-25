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

Install the .NET 8 SDK.

From the repository root:

```powershell
dotnet new sln -n SliceControl

dotnet sln SliceControl.sln add .\src\SliceControl\SliceControl.csproj
dotnet sln SliceControl.sln add .\src\SliceCtl\SliceCtl.csproj

dotnet build
```

Release build:

```powershell
dotnet publish .\src\SliceCtl\SliceCtl.csproj -c Release -r win-x64 --self-contained false
```

## Discover the device

```powershell
slicectl devices
```

The program discovers the Slice dynamically by:

```text
VID_03F0
PID_0D66
COL01
COL02
COL03
COL04
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
21:10:44.381  VolumeDown Down [report=0x31, value=0x02]
21:10:44.497  VolumeDown Up [report=0x31, value=0x00]
21:10:47.114  PhoneMute Down [report=0x32, value=0x10]
21:10:47.275  PhoneMute Up [report=0x32, value=0x00]
```

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

See:

`protocol/README.md`

for confirmed HID reports and LED behavior.