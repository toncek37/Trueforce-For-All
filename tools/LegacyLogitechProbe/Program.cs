using System;
using System.Threading;
using TrueforceForAll.Core;

internal static class Program
{
    private static int Main(string[] args)
    {
        Console.WriteLine("Trueforce For All - Legacy Logitech FFB probe");
        Console.WriteLine("G29/G920 classic Logitech Steering Wheel SDK test");
        Console.WriteLine();
        Console.WriteLine("IMPORTANT: keep hands clear enough that the wheel can move a little.");
        Console.WriteLine("The test uses only low forces (20%) and stops every effect on exit.");
        Console.WriteLine("Close Farming Simulator and G HUB before testing. LGS may remain installed.");
        Console.WriteLine();

        using (var ffb = new LegacyLogitechFfbOutput(Console.WriteLine))
        {
            Console.WriteLine("Initializing Logitech SDK...");
            if (!ffb.TryInitialize())
            {
                Console.WriteLine();
                Console.WriteLine("FAILED: no usable Logitech FFB wheel was opened.");
                Console.WriteLine("Check that the G29 is connected in PS4 mode and visible in joy.cpl.");
                Console.WriteLine("Expected SDK path:");
                Console.WriteLine(@"  C:\Program Files\Logitech Gaming Software\SDK\SteeringWheel\x86\LogitechSteeringWheel.dll");
                return 2;
            }

            Console.WriteLine();
            Console.WriteLine($"Opened controller index {ffb.ControllerIndex}.");
            Console.WriteLine("Press ENTER to run the low-force polarity test, or Q to quit.");
            if (ReadQuit()) return 0;

            try
            {
                // Keep pumping the SDK between writes. Logitech's SDK is stateful;
                // this also detects a disconnect before each force segment.
                if (!Pump(ffb, 150)) return Disconnect();

                Console.WriteLine();
                Console.WriteLine("1/4: +20% constant force for 0.7 s");
                if (!ffb.SetConstantForce(+20)) return WriteFail("constant +20");
                if (!Pump(ffb, 700)) return Disconnect();
                ffb.SetConstantForce(0);
                Pump(ffb, 350);

                Console.WriteLine("2/4: -20% constant force for 0.7 s");
                if (!ffb.SetConstantForce(-20)) return WriteFail("constant -20");
                if (!Pump(ffb, 700)) return Disconnect();
                ffb.SetConstantForce(0);
                Pump(ffb, 350);

                Console.WriteLine("3/4: 25% centering spring for 1.0 s");
                if (!ffb.SetSpring(0, 100, 25)) return WriteFail("spring");
                if (!Pump(ffb, 1000)) return Disconnect();
                ffb.SetSpring(0, 0, 0);
                Pump(ffb, 350);

                Console.WriteLine("4/4: 25% damper for 1.0 s - turn the wheel by hand");
                if (!ffb.SetDamper(25)) return WriteFail("damper");
                if (!Pump(ffb, 1000)) return Disconnect();
                ffb.SetDamper(0);
                Pump(ffb, 200);

                ffb.StopAll();
                Console.WriteLine();
                Console.WriteLine("TEST COMPLETE.");
                Console.WriteLine("Please note:");
                Console.WriteLine("  - which direction +20% pulled the wheel");
                Console.WriteLine("  - whether -20% pulled the opposite way");
                Console.WriteLine("  - whether the spring centered the wheel");
                Console.WriteLine("  - whether the damper made manual turning heavier");
                Console.WriteLine();
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

    private static int WriteFail(string effect)
    {
        Console.WriteLine($"FAILED: Logitech SDK rejected {effect} effect.");
        return 4;
    }
}
