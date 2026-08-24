using System;
using System.IO;
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
    /// project. Before the first P/Invoke we try to preload the x86 SDK from
    /// the normal Logitech Gaming Software install locations. This matters for
    /// SimHub because its 32-bit process does not normally search LGS's SDK
    /// subdirectory. Missing SDK files disable this backend cleanly without
    /// affecting Trueforce wheels.
    /// </summary>
    public sealed class LegacyLogitechFfbOutput : IDisposable
    {
        public const int MaxControllers = 2;

        private readonly Action<string> _log;
        private int _index = -1;
        private bool _sdkInitialized;
        private bool _disposed;
        private IntPtr _sdkModule;
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
        ///
        /// Logitech ships two initialization entry points. The simple one is
        /// tried first; if it refuses to initialize we retry with an HWND. A
        /// caller may supply the host window explicitly (useful from SimHub),
        /// otherwise we resolve a console/active/foreground window best-effort.
        /// </summary>
        public bool TryInitialize(IntPtr ownerWindow = default(IntPtr))
        {
            ThrowIfDisposed();
            if (IsReady) return true;

            try
            {
                TryPreloadSdk();

                // FS/SimHub primarily sees DirectInput wheels. Ignoring XInput
                // controllers avoids accidentally selecting an unrelated pad.
                bool initialized = Native.LogiSteeringInitialize(true);
                if (!initialized)
                {
                    _log("[LegacyLogitechFFB] LogiSteeringInitialize returned false; trying LogiSteeringInitializeWithWindow fallback.");
                    IntPtr hwnd = ownerWindow != IntPtr.Zero ? ownerWindow : ResolveOwnerWindow();
                    if (hwnd != IntPtr.Zero)
                    {
                        initialized = Native.LogiSteeringInitializeWithWindow(true, hwnd);
                        _log(initialized
                            ? $"[LegacyLogitechFFB] LogiSteeringInitializeWithWindow succeeded (HWND 0x{hwnd.ToInt64():X})."
                            : $"[LegacyLogitechFFB] LogiSteeringInitializeWithWindow returned false (HWND 0x{hwnd.ToInt64():X}).");
                    }
                    else
                    {
                        _log("[LegacyLogitechFFB] No usable owner window was available for the WithWindow fallback.");
                    }
                }

                if (!initialized)
                    return false;

                _sdkInitialized = true;
                Native.LogiUpdate();

                for (int i = 0; i < MaxControllers; i++)
                {
                    if (!Native.LogiIsConnected(i)) continue;
                    if (!Native.LogiHasForceFeedback(i)) continue;

                    _index = i;
                    // Do not call LogiGetFriendlyProductName here. Logitech shipped
                    // multiple incompatible signatures for that helper across SDK
                    // revisions; calling the wrong form can raise AccessViolation.
                    // Device identity is not needed for FFB operation.
                    _log($"[LegacyLogitechFFB] Connected to FFB controller {i}.");
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
            if (_sdkModule != IntPtr.Zero)
            {
                try { Native.FreeLibrary(_sdkModule); } catch { }
                _sdkModule = IntPtr.Zero;
            }
            _disposed = true;
        }

        /// <summary>
        /// Preload the x86 Logitech Steering Wheel SDK from LGS. DllImport then
        /// binds to the already-loaded module by basename. The explicit
        /// TF4ALL_LOGITECH_SDK environment variable is useful for developers;
        /// it can point either at the DLL itself or at its containing folder.
        /// </summary>
        private void TryPreloadSdk()
        {
            if (_sdkModule != IntPtr.Zero) return;

            string explicitPath = Environment.GetEnvironmentVariable("TF4ALL_LOGITECH_SDK");
            string programW6432 = Environment.GetEnvironmentVariable("ProgramW6432");
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

            string[] candidates =
            {
                ExpandSdkCandidate(explicitPath),
                CombineSdkPath(programW6432),
                CombineSdkPath(programFiles),
                CombineSdkPath(programFilesX86),
            };

            foreach (string path in candidates)
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) continue;
                IntPtr h = Native.LoadLibrary(path);
                if (h == IntPtr.Zero)
                {
                    int err = Marshal.GetLastWin32Error();
                    _log($"[LegacyLogitechFFB] Found Logitech SDK at '{path}', but LoadLibrary failed (Win32 {err}).");
                    continue;
                }

                _sdkModule = h;
                _log($"[LegacyLogitechFFB] Loaded Logitech Steering Wheel SDK: {path}");
                return;
            }

            _log("[LegacyLogitechFFB] Logitech SDK was not found in the standard LGS SteeringWheel\\x86 folders; falling back to the normal DLL search path.");
        }

        private static string ExpandSdkCandidate(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            path = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
            if (path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) return path;
            return Path.Combine(path, "LogitechSteeringWheel.dll");
        }

        private static string CombineSdkPath(string programFilesRoot)
        {
            if (string.IsNullOrWhiteSpace(programFilesRoot)) return null;
            return Path.Combine(programFilesRoot, "Logitech Gaming Software", "SDK", "SteeringWheel", "x86", "LogitechSteeringWheel.dll");
        }

        private static IntPtr ResolveOwnerWindow()
        {
            // Console probe first; WPF/WinForms hosts generally have an active
            // or foreground window even though GetConsoleWindow is zero.
            IntPtr hwnd = Native.GetConsoleWindow();
            if (hwnd != IntPtr.Zero) return hwnd;
            hwnd = Native.GetActiveWindow();
            if (hwnd != IntPtr.Zero) return hwnd;
            return Native.GetForegroundWindow();
        }

        private void ShutdownSdk()
        {
            _index = -1;
            if (!_sdkInitialized) return;
            try { Native.LogiSteeringShutdown(); } catch { }
            _sdkInitialized = false;
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

            [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
            internal static extern IntPtr LoadLibrary(string lpFileName);

            [DllImport("kernel32", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool FreeLibrary(IntPtr hModule);

            [DllImport("kernel32")]
            internal static extern IntPtr GetConsoleWindow();

            [DllImport("user32")]
            internal static extern IntPtr GetActiveWindow();

            [DllImport("user32")]
            internal static extern IntPtr GetForegroundWindow();

            [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
            [return: MarshalAs(UnmanagedType.I1)]
            internal static extern bool LogiSteeringInitialize([MarshalAs(UnmanagedType.I1)] bool ignoreXInputControllers);

            [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
            [return: MarshalAs(UnmanagedType.I1)]
            internal static extern bool LogiSteeringInitializeWithWindow(
                [MarshalAs(UnmanagedType.I1)] bool ignoreXInputControllers,
                IntPtr windowHandle);

            [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
            [return: MarshalAs(UnmanagedType.I1)]
            internal static extern bool LogiUpdate();

            [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
            [return: MarshalAs(UnmanagedType.I1)]
            internal static extern bool LogiIsConnected(int index);

            [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
            [return: MarshalAs(UnmanagedType.I1)]
            internal static extern bool LogiHasForceFeedback(int index);

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
