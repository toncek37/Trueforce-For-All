# LegacyFarmingLiveProbe

Standalone first-live-test bridge for Farming Simulator 25 + Logitech G29/G920.

It intentionally runs outside SimHub so the legacy Logitech path can be validated before touching the main plugin lifecycle.

## Safety model

- Default is **monitor only**. No FFB is written.
- `--enable-output` is required before any wheel force is sent.
- First live output is capped to **50% of the model gain**.
- A 300 ms telemetry deadman stops all effects if the FS telemetry stream stalls.
- Leaving the vehicle, disconnecting the pipe, Ctrl+C, or process shutdown stops all effects.
- `--invert` flips only Constant Force polarity after the hardware polarity test tells us which sign is correct.

## Requirements

- TF4ALL Enhanced Telemetry Farming Simulator mod installed/enabled.
- Logitech Gaming Software installed with the x86 Steering Wheel SDK.
- G29 in PS4 mode / visible in `joy.cpl`.
- Do not run the normal Trueforce For All SimHub plugin at the same time as this standalone probe: both want to own the `TF4ALLTelemetry` named pipe.

## Commands

Build/parser self-test, no wheel and no game required:

```powershell
dotnet run --project .\tools\LegacyFarmingLiveProbe\LegacyFarmingLiveProbe.csproj -c Release -- --self-test
```

Live telemetry monitor, no wheel output:

```powershell
dotnet run --project .\tools\LegacyFarmingLiveProbe\LegacyFarmingLiveProbe.csproj -c Release
```

First guarded live G29 output (only after the standalone hardware probe passes):

```powershell
dotnet run --project .\tools\LegacyFarmingLiveProbe\LegacyFarmingLiveProbe.csproj -c Release -- --enable-output
```

If the Constant Force polarity is backwards:

```powershell
dotnet run --project .\tools\LegacyFarmingLiveProbe\LegacyFarmingLiveProbe.csproj -c Release -- --enable-output --invert
```

## What it currently uses from FS telemetry

- vehicle speed
- motor load
- attached fill
- towed mass
- per-wheel surface speed/load to derive slip
- wheel contact flags to derive airborne state

Physical steering position is read directly from the Logitech Steering Wheel SDK (`LogiGetState`) rather than relying on FS steering telemetry.
