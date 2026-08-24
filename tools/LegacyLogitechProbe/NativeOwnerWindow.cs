using System;
using System.Runtime.InteropServices;

internal sealed class NativeOwnerWindow : IDisposable
{
    private const string ClassName = "TF4ALL_LegacyLogitechProbe_Window";
    private static readonly WndProc WndProcDelegate = WindowProc;
    private readonly IntPtr _hInstance;
    private readonly ushort _classAtom;

    public IntPtr Handle { get; private set; }

    public NativeOwnerWindow()
    {
        _hInstance = GetModuleHandle(null);

        var wc = new WNDCLASS
        {
            lpfnWndProc = WndProcDelegate,
            hInstance = _hInstance,
            lpszClassName = ClassName,
        };

        _classAtom = RegisterClass(ref wc);
        if (_classAtom == 0)
        {
            int error = Marshal.GetLastWin32Error();
            // ERROR_CLASS_ALREADY_EXISTS = 1410. Reusing the class is fine.
            if (error != 1410)
                throw new InvalidOperationException($"RegisterClass failed (Win32 {error}).");
        }

        Handle = CreateWindowEx(
            0,
            ClassName,
            "TF4ALL Logitech SDK Host",
            0,
            0, 0, 1, 1,
            IntPtr.Zero,
            IntPtr.Zero,
            _hInstance,
            IntPtr.Zero);

        if (Handle == IntPtr.Zero)
            throw new InvalidOperationException($"CreateWindowEx failed (Win32 {Marshal.GetLastWin32Error()}).");
    }

    public void Dispose()
    {
        if (Handle != IntPtr.Zero)
        {
            DestroyWindow(Handle);
            Handle = IntPtr.Zero;
        }

        if (_classAtom != 0)
            UnregisterClass(ClassName, _hInstance);
    }

    private static IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        => DefWindowProc(hwnd, msg, wParam, lParam);

    private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASS
    {
        public uint style;
        public WndProc lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string lpszMenuName;
        public string lpszClassName;
    }

    [DllImport("kernel32", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    [DllImport("user32", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClass(ref WNDCLASS lpWndClass);

    [DllImport("user32", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool UnregisterClass(string lpClassName, IntPtr hInstance);

    [DllImport("user32", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        uint dwExStyle,
        string lpClassName,
        string lpWindowName,
        uint dwStyle,
        int x,
        int y,
        int nWidth,
        int nHeight,
        IntPtr hWndParent,
        IntPtr hMenu,
        IntPtr hInstance,
        IntPtr lpParam);

    [DllImport("user32")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}
