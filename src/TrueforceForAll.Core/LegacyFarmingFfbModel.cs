using System;

namespace TrueforceForAll.Core
{
    /// <summary>
    /// First-pass low-frequency steering model for legacy Logitech wheels
    /// (G29/G920). It deliberately uses only telemetry already available from
    /// the FS enhanced source plus the wheel's physical steering position.
    /// The output maps directly to the classic Logitech SDK effects.
    /// </summary>
    public sealed class LegacyFarmingFfbModel
    {
        public struct Input
        {
            public double SpeedKmh;
            public double SteeringNorm;      // -1..1 physical wheel position
            public double SteeringVelocity;  // steer-units / second
            public double MotorLoad01;       // -1 unknown, otherwise 0..1
            public double TowedMassKg;
            public double AttachedFill01;
            public double? WheelSlip01;
            public bool? Airborne;
        }

        public struct Command
        {
            public int ConstantForce; // -100..100
            public int SpringOffset;  // -100..100
            public int SpringSaturation;
            public int SpringCoefficient;
            public int DamperCoefficient;
        }

        public double MasterGain { get; set; } = 1.0;
        public bool InvertForce { get; set; }

        public Command Evaluate(Input i)
        {
            double speed = Math.Max(0.0, i.SpeedKmh);
            double steer = Clamp(i.SteeringNorm, -1.0, 1.0);

            // Hydraulic/mechanical steering weight: strongest at standstill,
            // falling quickly once the tires are rolling.
            double lowSpeedWeight = Lerp(0.82, 0.18, Smooth01(speed / 18.0));

            // Self-centering becomes more important with road speed. Keep a
            // small floor at zero speed so the wheel never feels disconnected.
            double spring = Lerp(0.22, 0.62, Smooth01(speed / 35.0));

            // Attached mass/load makes agricultural steering feel heavier.
            double massBoost = Clamp(i.TowedMassKg / 12000.0, 0.0, 0.18);
            double fillBoost = Clamp(i.AttachedFill01, 0.0, 1.0) * 0.08;
            double motorBoost = i.MotorLoad01 >= 0.0 ? Clamp(i.MotorLoad01, 0.0, 1.0) * 0.08 : 0.0;
            lowSpeedWeight += massBoost + fillBoost + motorBoost;

            // Reduce tire-generated forces when airborne or badly slipping.
            double gripScale = 1.0;
            if (i.Airborne == true) gripScale = 0.05;
            else if (i.WheelSlip01.HasValue)
                gripScale *= 1.0 - 0.55 * Clamp(i.WheelSlip01.Value, 0.0, 1.0);

            // Constant force opposes displacement from center. Spring handles
            // the broad centering curve; constant force adds the heavy tire
            // scrub that is most noticeable while stationary / crawling.
            double constant = -steer * lowSpeedWeight * gripScale;

            // Extra viscous damping suppresses gear-rattle and gives the G29
            // useful steering effort at tiny speeds without an excessive spring.
            double damper = Lerp(0.58, 0.16, Smooth01(speed / 22.0));
            damper += Math.Min(Math.Abs(i.SteeringVelocity) * 0.03, 0.08);

            constant *= MasterGain;
            spring *= MasterGain * gripScale;
            damper *= MasterGain;

            int cf = ToSignedPercent(constant);
            if (InvertForce) cf = -cf;

            return new Command
            {
                ConstantForce = cf,
                SpringOffset = 0,
                SpringSaturation = 100,
                SpringCoefficient = ToUnsignedPercent(spring),
                DamperCoefficient = ToUnsignedPercent(damper),
            };
        }

        private static int ToSignedPercent(double v)
            => (int)Math.Round(Clamp(v, -1.0, 1.0) * 100.0);

        private static int ToUnsignedPercent(double v)
            => (int)Math.Round(Clamp(v, 0.0, 1.0) * 100.0);

        private static double Clamp(double v, double lo, double hi)
            => v < lo ? lo : (v > hi ? hi : v);

        private static double Lerp(double a, double b, double t)
            => a + (b - a) * Clamp(t, 0.0, 1.0);

        private static double Smooth01(double x)
        {
            x = Clamp(x, 0.0, 1.0);
            return x * x * (3.0 - 2.0 * x);
        }
    }
}
