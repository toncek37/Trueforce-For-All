using System;
using System.Threading;
using TrueforceForAll.Core;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length > 0 && string.Equals(args[0], "--dry-run", StringComparison.OrdinalIgnoreCase))
            return RunModelDryRun();

        Console.WriteLine("Trueforce For All - Legacy Logitech FFB probe");
        Console.WriteLine("G29/G920 classic Logitech Steering Wheel SDK test");
        Console.WriteLine();
        Console.WriteLine("IMPORTANT: keep hands clear enough that the wheel can move a little.");
        Console.WriteLine("The test uses only low forces and stops every effect on exit.");
        Console.WriteLine("Close Farming Simulator and G HUB before testing. LGS may remain installed.");
        Console.WriteLine();

        using (var ownerWindow = new NativeOwnerWindow())
        using (var ffb = new LegacyLogitechFfbOutput(Console.WriteLine))
        {
            Console.WriteLine($"Created process-owned SDK window: HWND 0x{ownerWindow.Handle.ToInt64():X}");
            Console.WriteLine("Initializing Logitech SDK...");
            if (!ffb.TryInitialize(ownerWindow.Handle))
            {
                Console.WriteLine();
                Console.WriteLine("FAILED: no usable Logitech FFB wheel was opened.");
                Console.WriteLine("Check that the G29 is connected in PS4 mode and visible in joy.cpl.");
                return 2;
            }

            Console.WriteLine();
            Console.WriteLine($"Opened controller index {ffb.ControllerIndex}.");
            Console.WriteLine("Press ENTER to run the independent effect test, or Q to quit.");
            if (ReadQuit()) return 0;

            bool constantPlus = false;
            bool constantMinus = false;
            bool spring = false;
            bool damper = false;

            try
            {
                if (!Pump(ffb, 150)) return Disconnect();

                Console.WriteLine();
                Console.WriteLine("1/4: +20% constant force for 0.7 s");
                constantPlus = ffb.SetConstantForce(+20);
                Console.WriteLine(constantPlus ? "  SDK: ACCEPTED" : "  SDK: REJECTED");
                if (constantPlus)
                {
                    if (!Pump(ffb, 700)) return Disconnect();
                    ffb.SetConstantForce(0);
                }
                Pump(ffb, 350);

                Console.WriteLine("2/4: -20% constant force for 0.7 s");
                constantMinus = ffb.SetConstantForce(-20);
                Console.WriteLine(constantMinus ? "  SDK: ACCEPTED" : "  SDK: REJECTED");
                if (constantMinus)
                {
                    if (!Pump(ffb, 700)) return Disconnect();
                    ffb.SetConstantForce(0);
                }
                Pump(ffb, 350);

                Console.WriteLine("3/4: 25% centering spring for 1.0 s");
                spring = ffb.SetSpring(0, 100, 25);
                Console.WriteLine(spring ? "  SDK: ACCEPTED" : "  SDK: REJECTED");
                if (spring)
                {
                    if (!Pump(ffb, 1000)) return Disconnect();
                    ffb.SetSpring(0, 0, 0);
                }
                Pump(ffb, 350);

                Console.WriteLine("4/4: 25% damper for 1.5 s - turn the wheel by hand");
                damper = ffb.SetDamper(25);
                Console.WriteLine(damper ? "  SDK: ACCEPTED" : "  SDK: REJECTED");
                if (damper)
                {
                    if (!Pump(ffb, 1500)) return Disconnect();
                    ffb.SetDamper(0);
                }
                Pump(ffb, 200);

                ffb.StopAll();
                Console.WriteLine();
                Console.WriteLine("RESULTS");
                Console.WriteLine($"  constant +20 : {(constantPlus ? "ACCEPTED" : "REJECTED")}");
                Console.WriteLine($"  constant -20 : {(constantMinus ? "ACCEPTED" : "REJECTED")}");
                Console.WriteLine($"  spring       : {(spring ? "ACCEPTED" : "REJECTED")}");
                Console.WriteLine($"  damper       : {(damper ? "ACCEPTED" : "REJECTED")}");
                Console.WriteLine();
                Console.WriteLine("Also note what you physically felt for every ACCEPTED effect.");
                Console.WriteLine("Press ENTER to exit.");
                Console.ReadLine();
                return 0;
            }
            finally
            {
                ffb.StopAll();
            }
        }
    }

    private static int RunModelDryRun()
    {
        Console.WriteLine("Trueforce For All - FS legacy FFB model dry-run");
        Console.WriteLine("No wheel or Logitech SDK initialization is used.");
        Console.WriteLine();

        var model = new LegacyFarmingFfbModel();
        double[] speeds = { 0, 5, 15, 40 };
        foreach (double speed in speeds)
        {
            var cmd = model.Evaluate(new LegacyFarmingFfbModel.Input
            {
                SpeedKmh = speed,
                SteeringNorm = 0.40,
                SteeringVelocity = 0.0,
                MotorLoad01 = 0.5,
                TowedMassKg = 3000,
                AttachedFill01 = 0.5,
                WheelSlip01 = 0.0,
                Airborne = false,
            });

            Console.WriteLine($"{speed,4:0} km/h : constant={cmd.ConstantForce,4}%  spring={cmd.SpringCoefficient,3}%  damper={cmd.DamperCoefficient,3}%");
        }

        Console.WriteLine();
        Console.WriteLine("Expected trend: heavy scrub/damping at 0 km/h, less constant force as speed rises, stronger self-centering spring at road speed.");
        return 0;
    }

    private static bool Pump(LegacyLogitechFfbOutput ffb, int milliseconds)
    {
        const int StepMs = 10;
        int left = milliseconds;
        while (left > 0)
        {
            if (!ffb.Update()) return false;
            int delay = Math.Min(StepMs, left);
            Thread.Sleep(delay);
            left -= delay;
        }
        return true;
    }

    private static bool ReadQuit()
    {
        var key = Console.ReadKey(true);
        if (key.Key == ConsoleKey.Q) return true;
        if (key.Key != ConsoleKey.Enter)
        {
            Console.WriteLine("Press ENTER to continue or Q to quit.");
            return ReadQuit();
        }
        return false;
    }

    private static int Disconnect()
    {
        Console.WriteLine("FAILED: wheel disconnected or Logitech SDK update failed.");
        return 3;
    }
}
