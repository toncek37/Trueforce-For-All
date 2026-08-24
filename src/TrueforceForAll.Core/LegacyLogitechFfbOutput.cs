using System;
using System.Runtime.InteropServices;

namespace TrueforceForAll.Core
{
    /// <summary>
    /// Low-frequency force-feedback output for legacy Logitech wheels such as
    /// the G29/G920. Unlike G923/G PRO/RS50 these wheels do not expose the
    /// Trueforce audio-haptic endpoint, so telemetry-generated steering torque
    /// has to be sent through Logitech's classic Steering Wheel SDK.
    ///
    /// The native Logitech SDK DLL is intentionally not redistributed by this
    /// project. The loader resolves it at runtime from the process DLL search
    /// path (normally next to SimHub.exe / the plugin, or another location
    /// added by the user). Missing SDK files therefore disable this backend
    /// cleanly without affecting Trueforce wheels.
    /// </summary>
    public sealed class LegacyLogitechFfbOutput : IDisposable
    {
        public const int MaxControllers = 2;

        private readonly Action<string> _log;
        private int _index = -1;
        private bool _sdkInitialized;
        private bool _disposed;
        private int _lastConstantForce = int.MinValue;
        private int _lastDamper = int.MinValue;
        private int _lastSpringOffset = int.MinValue;
        private int _lastSpringSaturation = int.MinValue;
        private int _lastSpringCoefficient = int.MinValue;

        public bool IsReady => _sdkInitialized && _index >= 0;
        public int ControllerIndex => _index;

        public LegacyLogitechFfbOutput(Action<string> log = null)
        {
            _log = log ?? (_ => { });
        }

        /// <summary>
        /// Initializes the Logitech Steering Wheel SDK and selects the first
        /// connected Logitech force-feedback controller. This is deliberately
        /// capability based rather than model-number based so G29 and G920 can
        /// share the same path.
        /// </summary>
        public bool TryInitialize()
        {
            ThrowIfDisposed();
            if (IsReady) return true;

            try
            {
                // FS/SimHub primarily sees DirectInput wheels. Ignoring XInput
                // controllers avoids accidentally selecting an unrelated pad.
                if (!Native.LogiSteeringInitialize(true))
                {
                    _log("[LegacyLogitechFFB] LogiSteeringInitialize returned false.");
                    return false;
                }

                _sdkInitialized = true;
                Native.LogiUpdate();

                for (int i = 0; i < MaxControllers; i++)
                {
                    if (!Native.LogiIsConnected(i)) continue;
                    if (!Native.LogiHasForceFeedback(i)) continue;

                    _index = i;
                    string name = TryGetFriendlyName(i);
                    _log($"[LegacyLogitechFFB] Connected to controller {i}{(string.IsNullOrWhiteSpace(name) ? string.Empty : $" ({name})")}.");
                    return true;
                }

                _log("[LegacyLogitechFFB] SDK initialized, but no Logitech force-feedback wheel was found.");
                ShutdownSdk();
                return false;
            }
            catch (DllNotFoundException ex)
            {
                _log($"[LegacyLogitechFFB] Logitech Steering Wheel SDK DLL not found: {ex.Message}");
                ShutdownSdk();
                return false;
            }
            catch (EntryPointNotFoundException ex)
            {
                _log($"[LegacyLogitechFFB] Incompatible Logitech Steering Wheel SDK DLL: {ex.Message}");
                ShutdownSdk();
                return false;
            }
            catch (BadImageFormatException ex)
            {
                _log($"[LegacyLogitechFFB] Logitech SDK bitness does not match SimHub: {ex.Message}");
                ShutdownSdk();
                return false;
            }
            catch (Exception ex)
            {
                _log($"[LegacyLogitechFFB] Initialization failed: {ex.GetType().Name}: {ex.Message}");
                ShutdownSdk();
                return false;
            }
        }

        /// <summary>
        /// Pumps the Logitech SDK. Call regularly from the plugin update loop.
        /// Returns false if the selected controller is no longer connected.
        /// </summary>
        public bool Update()
        {
            if (!IsReady || _disposed) return false;
            try
            {
                if (!Native.LogiUpdate()) return false;
                return Native.LogiIsConnected(_index);
            }
            catch (Exception ex)
            {
                _log($"[LegacyLogitechFFB] Update failed: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Sends a signed constant steering force in the Logitech SDK range
        /// [-100, 100]. Positive/negative polarity can be inverted by the
        /// caller once the G29 hardware test establishes the desired sign.
        /// </summary>
        public bool SetConstantForce(int percent)
        {
            if (!IsReady || _disposed) return false;
            percent = Clamp(percent, -100, 100);
            if (percent == _lastConstantForce) return true;

            try
            {
                bool ok = percent == 0
                    ? Native.LogiStopConstantForce(_index)
                    : Native.LogiPlayConstantForce(_index, percent);
                if (ok) _lastConstantForce = percent;
                return ok;
            }
            catch (Exception ex)
            {
                _log($"[LegacyLogitechFFB] Constant force write failed: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Adds viscous resistance. Logitech expects coefficient [0, 100].
        /// Useful for hydraulic steering weight at very low vehicle speed.
        /// </summary>
        public bool SetDamper(int coefficientPercent)
        {
            if (!IsReady || _disposed) return false;
            coefficientPercent = Clamp(coefficientPercent, 0, 100);
            if (coefficientPercent == _lastDamper) return true;

            try
            {
                bool ok = coefficientPercent == 0
                    ? Native.LogiStopDamperForce(_index)
                    : Native.LogiPlayDamperForce(_index, coefficientPercent);
                if (ok) _lastDamper = coefficientPercent;
                return ok;
            }
            catch (Exception ex)
            {
                _log($"[LegacyLogitechFFB] Damper write failed: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Configures a centering spring. Offset is [-100,100], saturation and
        /// coefficient are [0,100]. Passing coefficient 0 stops the spring.
        /// </summary>
        public bool SetSpring(int offsetPercent, int saturationPercent, int coefficientPercent)
        {
            if (!IsReady || _disposed) return false;
            offsetPercent = Clamp(offsetPercent, -100, 100);
            saturationPercent = Clamp(saturationPercent, 0, 100);
            coefficientPercent = Clamp(coefficientPercent, 0, 100);

            if (offsetPercent == _lastSpringOffset &&
                saturationPercent == _lastSpringSaturation &&
                coefficientPercent == _lastSpringCoefficient)
                return true;

            try
            {
                bool ok = coefficientPercent == 0
                    ? Native.LogiStopSpringForce(_index)
                    : Native.LogiPlaySpringForce(_index, offsetPercent, saturationPercent, coefficientPercent);
                if (ok)
                {
                    _lastSpringOffset = offsetPercent;
                    _lastSpringSaturation = saturationPercent;
                    _lastSpringCoefficient = coefficientPercent;
                }
                return ok;
            }
            catch (Exception ex)
            {
                _log($"[LegacyLogitechFFB] Spring write failed: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        public void StopAll()
        {
            if (!IsReady || _disposed) return;
            try { Native.LogiStopConstantForce(_index); } catch { }
            try { Native.LogiStopSpringForce(_index); } catch { }
            try { Native.LogiStopDamperForce(_index); } catch { }
            _lastConstantForce = int.MinValue;
            _lastDamper = int.MinValue;
            _lastSpringOffset = int.MinValue;
            _lastSpringSaturation = int.MinValue;
            _lastSpringCoefficient = int.MinValue;
        }

        public void Dispose()
        {
            if (_disposed) return;
            StopAll();
            ShutdownSdk();
            _disposed = true;
        }

        private void ShutdownSdk()
        {
            _index = -1;
            if (!_sdkInitialized) return;
            try { Native.LogiSteeringShutdown(); } catch { }
            _sdkInitialized = false;
        }

        private static string TryGetFriendlyName(int index)
        {
            try
            {
                IntPtr ptr = Native.LogiGetFriendlyProductName(index);
                return ptr == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(ptr);
            }
            catch
            {
                return null;
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(LegacyLogitechFfbOutput));
        }

        private static int Clamp(int value, int min, int max)
            => value < min ? min : (value > max ? max : value);

        private static class Native
        {
            // The SDK examples import "LogitechSteeringWheel"; Windows adds
            // the .dll suffix automatically. Use cdecl as specified by the SDK.
            private const string DllName = "LogitechSteeringWheel";

            [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
            [return: MarshalAs(UnmanagedType.I1)]
            internal static extern bool LogiSteeringInitialize([MarshalAs(UnmanagedType.I1)] bool ignoreXInputControllers);

            [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
            [return: MarshalAs(UnmanagedType.I1)]
            internal static extern bool LogiUpdate();

            [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
            [return: MarshalAs(UnmanagedType.I1)]
            internal static extern bool LogiIsConnected(int index);

            [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
            [return: MarshalAs(UnmanagedType.I1)]
            internal static extern bool LogiHasForceFeedback(int index);

            [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
            internal static extern IntPtr LogiGetFriendlyProductName(int index);

            [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
            [return: MarshalAs(UnmanagedType.I1)]
            internal static extern bool LogiPlayConstantForce(int index, int magnitudePercentage);

            [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
            [return: MarshalAs(UnmanagedType.I1)]
            internal static extern bool LogiStopConstantForce(int index);

            [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
            [return: MarshalAs(UnmanagedType.I1)]
            internal static extern bool LogiPlaySpringForce(int index, int offsetPercentage, int saturationPercentage, int coefficientPercentage);

            [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
            [return: MarshalAs(UnmanagedType.I1)]
            internal static extern bool LogiStopSpringForce(int index);

            [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
            [return: MarshalAs(UnmanagedType.I1)]
            internal static extern bool LogiPlayDamperForce(int index, int coefficientPercentage);

            [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
            [return: MarshalAs(UnmanagedType.I1)]
            internal static extern bool LogiStopDamperForce(int index);

            [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
            internal static extern void LogiSteeringShutdown();
        }
    }
}
