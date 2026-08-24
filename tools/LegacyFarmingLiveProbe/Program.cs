using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;
using TrueforceForAll.Core;

internal static class Program
{
    private const string PipeName = "TF4ALLTelemetry";
    private static volatile bool _quit;
    private static Form _sdkHost;

    [STAThread]
    private static async Task<int> Main(string[] args)
    {
        bool enableOutput = HasArg(args, "--enable-output");
        bool invert = HasArg(args, "--invert");
        bool selfTest = HasArg(args, "--self-test");

        if (selfTest)
            return RunSelfTest();

        Console.WriteLine("Trueforce For All - FS25 legacy Logitech live probe");
        Console.WriteLine("Reads TF4ALL Enhanced Telemetry and computes G29/G920 low-frequency FFB.");
        Console.WriteLine();
        Console.WriteLine(enableOutput
            ? "OUTPUT ENABLED: forces will be sent to the wheel."
            : "MONITOR ONLY: Logitech SDK is opened only to read wheel position; no active FFB effects are generated.");
        if (invert) Console.WriteLine("Constant-force polarity inversion ENABLED.");
        Console.WriteLine("Ctrl+C exits and stops all effects.");
        Console.WriteLine();

        Console.CancelKeyPress += (_, e) => { e.Cancel = true; _quit = true; };

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        _sdkHost = new Form
        {
            Text = "TF4ALL Logitech SDK Host",
            Width = 360,
            Height = 110,
            StartPosition = FormStartPosition.CenterScreen,
            TopMost = true,
        };
        _sdkHost.FormClosing += (_, e) =>
        {
            if (!_quit)
            {
                e.Cancel = true;
                _sdkHost.Hide();
            }
        };
        _sdkHost.Show();
        _sdkHost.Activate();
        Application.DoEvents();

        using var output = new LegacyLogitechFfbOutput(Console.WriteLine);
        LegacyFarmingFfbController controller = null;

        Console.WriteLine($"Created visible process-owned SDK window: HWND 0x{_sdkHost.Handle.ToInt64():X}");
        Console.WriteLine("Initializing Logitech wheel for steering input...");
        if (!output.TryInitialize(_sdkHost.Handle))
        {
            Console.WriteLine("FAILED: Logitech wheel could not be initialized.");
            _quit = true;
            _sdkHost.Close();
            return 2;
        }

        // Clear any stale SDK effects from an earlier probe. Monitor mode never starts effects after this.
        output.StopAll();

        if (enableOutput)
        {
            var model = new LegacyFarmingFfbModel
            {
                MasterGain = 0.50,
                InvertForce = invert,
            };
            controller = new LegacyFarmingFfbController(output, model);
            Console.WriteLine("Wheel opened. First live run is capped at 50% model gain.");
        }
        else
        {
            Console.WriteLine("Wheel opened for steering readback. FFB output remains disabled.");
        }
        Console.WriteLine();

        try
        {
            while (!_quit)
            {
                Console.WriteLine($"Waiting for Farming Simulator telemetry on \\.\\pipe\\{PipeName} ...");
                using var pipe = new NamedPipeServerStream(
                    PipeName, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

                Task waitTask = pipe.WaitForConnectionAsync();
                while (!_quit && !waitTask.IsCompleted)
                {
                    Application.DoEvents();
                    await Task.WhenAny(waitTask, Task.Delay(25));
                }
                if (_quit) break;
                try { await waitTask; }
                catch { continue; }

                Console.WriteLine("FS telemetry connected.");
                using var reader = new StreamReader(pipe);
                await RunConnected(reader, output, controller, enableOutput, invert);

                controller?.Stop();
                if (!_quit)
                    Console.WriteLine("Telemetry disconnected; forces stopped. Waiting for reconnect...");
            }
        }
        finally
        {
            controller?.Stop();
            controller?.Dispose();
            output.StopAll();
            _quit = true;
            _sdkHost?.Close();
            Application.DoEvents();
        }

        return 0;
    }

    private static async Task RunConnected(StreamReader reader, LegacyLogitechFfbOutput output,
                                           LegacyFarmingFfbController controller, bool enableOutput, bool invert)
    {
        long lastPrint = 0;
        long lastSteerTicks = 0;
        double lastSteer = 0;
        bool forcesStoppedForStall = false;
        var previewModel = controller?.Model ?? new LegacyFarmingFfbModel { MasterGain = 0.50, InvertForce = invert };

        while (!_quit)
        {
            Application.DoEvents();

            long readStarted = Stopwatch.GetTimestamp();
            Task<string> readTask = reader.ReadLineAsync();
            while (!_quit && !readTask.IsCompleted)
            {
                Application.DoEvents();
                Task winner = await Task.WhenAny(readTask, Task.Delay(50));
                if (winner == readTask) break;

                double waitingSeconds = (Stopwatch.GetTimestamp() - readStarted) / (double)Stopwatch.Frequency;
                if (enableOutput && !forcesStoppedForStall && waitingSeconds >= 0.30)
                {
                    controller?.Stop();
                    forcesStoppedForStall = true;
                    Console.WriteLine("Telemetry stalled >300 ms; forces stopped (deadman).");
                }
            }

            if (_quit) return;
            string line;
            try { line = await readTask; }
            catch { return; }
            if (line == null) return;
            if (line.Length == 0 || line[0] != '{') continue;

            FsSample s;
            try { s = Parse(line); }
            catch { continue; }

            if (!s.InVehicle)
            {
                controller?.Stop();
                forcesStoppedForStall = false;
                continue;
            }

            if (!output.Update()) return;

            double steer;
            bool haveSteer = NativeWheel.TryGetSteeringNorm(output.ControllerIndex, out steer);
            if (!haveSteer && enableOutput)
            {
                controller?.Stop();
                continue;
            }
            if (!haveSteer) steer = 0;

            double steerVel = 0;
            long now = Stopwatch.GetTimestamp();
            if (haveSteer && lastSteerTicks != 0)
            {
                double dt = (now - lastSteerTicks) / (double)Stopwatch.Frequency;
                if (dt > 0.002 && dt < 0.5) steerVel = (steer - lastSteer) / dt;
            }
            if (haveSteer)
            {
                lastSteer = steer;
                lastSteerTicks = now;
            }

            var input = new LegacyFarmingFfbModel.Input
            {
                SpeedKmh = s.SpeedKmh,
                SteeringNorm = haveSteer ? steer : 0,
                SteeringVelocity = steerVel,
                MotorLoad01 = s.MotorLoad01,
                TowedMassKg = s.TowedMassKg,
                AttachedFill01 = s.AttachedFill01,
                WheelSlip01 = s.WheelSlip01,
                Airborne = s.Airborne,
            };

            var cmd = previewModel.Evaluate(input);

            if (enableOutput)
            {
                if (!controller.Apply(input)) return;
                forcesStoppedForStall = false;
            }

            if (now - lastPrint > Stopwatch.Frequency / 4)
            {
                lastPrint = now;
                Console.WriteLine($"v={s.SpeedKmh,5:F1} km/h steer={(haveSteer ? steer.ToString("+0.00;-0.00;0.00") : " n/a"),5} " +
                                  $"slip={(s.WheelSlip01.HasValue ? s.WheelSlip01.Value.ToString("0.00") : "-")} " +
                                  $"air={(s.Airborne == true ? "Y" : "N")} -> CF={cmd.ConstantForce,4}% SP={cmd.SpringCoefficient,3}% DP={cmd.DamperCoefficient,3}%");
            }
        }
    }

    private static FsSample Parse(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement r = doc.RootElement;

        var s = new FsSample
        {
            InVehicle = GetBool(r, "inVehicle", false),
            SpeedKmh = GetDouble(r, "speedKmh", 0),
            MotorLoad01 = GetNullableDouble(r, "motorLoad") ?? -1,
            AttachedFill01 = GetDouble(r, "fill", 0),
            TowedMassKg = GetDouble(r, "towKg", 0),
        };

        if (r.TryGetProperty("wheels", out JsonElement wheels) && wheels.ValueKind == JsonValueKind.Array)
        {
            bool anyContactField = false;
            bool anyContact = false;
            double v = Math.Max(s.SpeedKmh / 3.6, 1.4);
            double slipNum = 0, slipDen = 0;
            double wsSum = 0; int wsN = 0;

            foreach (JsonElement w in wheels.EnumerateArray())
            {
                if (w.TryGetProperty("contact", out JsonElement c) && (c.ValueKind == JsonValueKind.True || c.ValueKind == JsonValueKind.False))
                {
                    anyContactField = true;
                    if (c.GetBoolean()) anyContact = true;
                }
                double? ws = GetNullableDouble(w, "ws");
                if (ws.HasValue) { wsSum += ws.Value; wsN++; }
            }

            double dir = wsN > 0 && Math.Abs(wsSum / wsN) > 0.6 ? Math.Sign(wsSum) : 1.0;
            foreach (JsonElement w in wheels.EnumerateArray())
            {
                bool contact = GetBool(w, "contact", true);
                if (!contact) continue;
                double? ws = GetNullableDouble(w, "ws");
                if (!ws.HasValue) continue;
                double groundSigned = (s.SpeedKmh / 3.6) * dir;
                double slip = Math.Min(Math.Abs(ws.Value - groundSigned) / v, 1.0);
                double load = Math.Max(GetDouble(w, "load", 1.0), 0.05);
                slipNum += slip * load;
                slipDen += load;
            }

            if (anyContactField) s.Airborne = !anyContact;
            if (slipDen > 0) s.WheelSlip01 = slipNum / slipDen;
        }

        return s;
    }

    private static int RunSelfTest()
    {
        const string sample = "{\"inVehicle\":true,\"speedKmh\":5.0,\"motorLoad\":0.4,\"fill\":0.5,\"towKg\":2500,\"wheels\":[{\"contact\":true,\"ws\":1.6,\"load\":1200},{\"contact\":true,\"ws\":1.5,\"load\":1200},{\"contact\":true,\"ws\":1.4,\"load\":1000},{\"contact\":true,\"ws\":1.4,\"load\":1000}]}";
        FsSample s = Parse(sample);
        var model = new LegacyFarmingFfbModel { MasterGain = 0.5 };
        var cmd = model.Evaluate(new LegacyFarmingFfbModel.Input
        {
            SpeedKmh = s.SpeedKmh,
            SteeringNorm = 0.4,
            SteeringVelocity = 0,
            MotorLoad01 = s.MotorLoad01,
            TowedMassKg = s.TowedMassKg,
            AttachedFill01 = s.AttachedFill01,
            WheelSlip01 = s.WheelSlip01,
            Airborne = s.Airborne,
        });
        Console.WriteLine($"SELF-TEST OK: v={s.SpeedKmh:F1}, slip={s.WheelSlip01:F3}, CF={cmd.ConstantForce}, SP={cmd.SpringCoefficient}, DP={cmd.DamperCoefficient}");
        return 0;
    }

    private static bool HasArg(string[] args, string wanted)
        => Array.Exists(args, a => string.Equals(a, wanted, StringComparison.OrdinalIgnoreCase));

    private static bool GetBool(JsonElement e, string name, bool fallback)
    {
        if (!e.TryGetProperty(name, out JsonElement v)) return fallback;
        if (v.ValueKind == JsonValueKind.True) return true;
        if (v.ValueKind == JsonValueKind.False) return false;
        return fallback;
    }

    private static double GetDouble(JsonElement e, string name, double fallback)
        => GetNullableDouble(e, name) ?? fallback;

    private static double? GetNullableDouble(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out JsonElement v) || v.ValueKind != JsonValueKind.Number) return null;
        return v.TryGetDouble(out double d) && !double.IsNaN(d) ? d : null;
    }

    private struct FsSample
    {
        public bool InVehicle;
        public double SpeedKmh;
        public double MotorLoad01;
        public double TowedMassKg;
        public double AttachedFill01;
        public double? WheelSlip01;
        public bool? Airborne;
    }

    private static class NativeWheel
    {
        [DllImport("LogitechSteeringWheel", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr LogiGetState(int index);

        public static bool TryGetSteeringNorm(int index, out double steer)
        {
            steer = 0;
            try
            {
                IntPtr p = LogiGetState(index);
                if (p == IntPtr.Zero) return false;
                int lx = Marshal.ReadInt32(p); // DIJOYSTATE2.lX, nominal 0..65535
                steer = lx / 32767.5 - 1.0;
                if (steer < -1) steer = -1;
                if (steer > 1) steer = 1;
                return true;
            }
            catch { return false; }
        }
    }
}
