# Legacy Logitech FFB probe (G29/G920)

Small **x86** console program used before wiring the legacy Logitech backend into the SimHub plugin.
It verifies that LGS's classic Steering Wheel SDK can open the wheel and play the three effects TF4ALL needs:

- signed constant force
- centering spring
- damper

The probe intentionally does **not** start Farming Simulator or SimHub integration yet. First we prove the hardware path and force polarity on a real G29.

## Expected SDK DLL

The backend auto-detects the LGS install used by the test machine:

```text
C:\Program Files\Logitech Gaming Software\SDK\SteeringWheel\x86\LogitechSteeringWheel.dll
```

A custom location can be supplied with `TF4ALL_LOGITECH_SDK` (either the DLL path or its directory).

## Run

Use a Windows machine with LGS and the G29 connected. Close Farming Simulator and G HUB first.

```powershell
dotnet run --project .\tools\LegacyLogitechProbe\LegacyLogitechProbe.csproj -c Release
```

The project targets .NET Framework 4.8 and **x86**, matching the SDK DLL and SimHub's 32-bit plugin host.

## Test sequence

After explicit ENTER confirmation the tool performs only low-force tests:

1. +20% constant force for 0.7 s
2. -20% constant force for 0.7 s
3. 25% centering spring for 1.0 s
4. 25% damper for 1.0 s

Every effect is stopped between stages and `StopAll()` also runs from `finally`/`Dispose` on exit.

Record four observations for the next implementation step:

- direction of +20%
- direction of -20%
- whether spring pulls toward centre
- whether damper makes hand rotation heavier

Once these are confirmed, the next step is to map TF4ALL's already-computed Farming Simulator steering torque into this backend and add speed/load dependent spring + damper.
