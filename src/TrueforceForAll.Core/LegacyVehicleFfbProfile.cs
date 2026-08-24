namespace TrueforceForAll.Core
{
    /// <summary>
    /// Vehicle-specific tuning layer for legacy Logitech FFB.
    /// Intentionally contains only neutral multipliers for now; concrete
    /// vehicle/class presets will be added after real G29 hardware tuning.
    /// </summary>
    public sealed class LegacyVehicleFfbProfile
    {
        public string Id { get; set; } = "default";
        public string DisplayName { get; set; } = "Default";

        /// <summary>Global multiplier applied to the complete legacy FFB model.</summary>
        public double MasterGain { get; set; } = 1.0;

        /// <summary>Multiplier for low-speed tire scrub / constant force.</summary>
        public double LowSpeedScrubGain { get; set; } = 1.0;

        /// <summary>Multiplier for centering spring strength.</summary>
        public double CenteringGain { get; set; } = 1.0;

        /// <summary>Multiplier for viscous damping / hydraulic steering weight.</summary>
        public double DamperGain { get; set; } = 1.0;

        /// <summary>Multiplier for trailer/implement mass contribution.</summary>
        public double LoadSensitivity { get; set; } = 1.0;

        /// <summary>Multiplier for fill-level contribution.</summary>
        public double FillSensitivity { get; set; } = 1.0;

        /// <summary>Multiplier for motor-load contribution.</summary>
        public double MotorLoadSensitivity { get; set; } = 1.0;

        /// <summary>
        /// Speed scale for steering-lightening/centering transitions.
        /// 1.0 keeps the baseline curve; values above 1.0 push transitions to
        /// higher road speed, values below 1.0 make them happen sooner.
        /// </summary>
        public double SpeedResponseScale { get; set; } = 1.0;

        /// <summary>
        /// Reserved vehicle steering classification for future handling rules.
        /// Examples: assisted, manual, articulated, four-wheel-steer.
        /// </summary>
        public LegacySteeringType SteeringType { get; set; } = LegacySteeringType.Unknown;

        public static LegacyVehicleFfbProfile Default => new LegacyVehicleFfbProfile();
    }

    public enum LegacySteeringType
    {
        Unknown = 0,
        Assisted = 1,
        Manual = 2,
        Articulated = 3,
        FourWheelSteer = 4,
    }
}
