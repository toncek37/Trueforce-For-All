using System;

namespace TrueforceForAll.Core
{
    /// <summary>
    /// Small adapter that evaluates the FS legacy steering model and writes the
    /// resulting low-frequency effects to the Logitech Steering Wheel SDK.
    /// Kept outside the SimHub plugin so it can be exercised by a console probe.
    /// </summary>
    public sealed class LegacyFarmingFfbController : IDisposable
    {
        private readonly LegacyLogitechFfbOutput _output;
        private readonly LegacyFarmingFfbModel _model;
        private bool _disposed;

        public LegacyFarmingFfbController(LegacyLogitechFfbOutput output, LegacyFarmingFfbModel model = null)
        {
            _output = output ?? throw new ArgumentNullException(nameof(output));
            _model = model ?? new LegacyFarmingFfbModel();
        }

        public LegacyFarmingFfbModel Model => _model;

        public bool Apply(LegacyFarmingFfbModel.Input input)
        {
            if (_disposed || !_output.IsReady) return false;
            if (!_output.Update()) return false;

            var c = _model.Evaluate(input);
            bool a = _output.SetConstantForce(c.ConstantForce);
            bool b = _output.SetSpring(c.SpringOffset, c.SpringSaturation, c.SpringCoefficient);
            bool d = _output.SetDamper(c.DamperCoefficient);
            return a && b && d;
        }

        public void Stop() => _output.StopAll();

        public void Dispose()
        {
            if (_disposed) return;
            try { Stop(); } catch { }
            _disposed = true;
        }
    }
}
