namespace Yako.Screenshot;

/// <summary>
/// Listens for Ctrl+PrtScn. Prefers RegisterHotKey; if another app already owns the
/// combination it falls back to a low-level keyboard hook so the app is never silently deaf.
/// </summary>
internal sealed class HotkeyListener : IDisposable
{
    private const int HotkeyId = 0x5941; // arbitrary, unique within this window

    private readonly MessageWindow _window;

    // The hook delegate must be kept alive in a field: if it is collected the hook dies
    // silently, with no error anywhere. This is the classic bug with WH_KEYBOARD_LL.
    private Native.LowLevelKeyboardProc? _hookProc;
    private IntPtr _hook;
    private IntPtr _registeredOn;
    private DateTime _lastFired = DateTime.MinValue;
    private bool _disposed;

    public event Action? Triggered;

    /// <summary>True when RegisterHotKey was refused and the keyboard hook is doing the work.</summary>
    public bool UsingFallback { get; private set; }

    public HotkeyListener()
    {
        _window = new MessageWindow(Fire);
    }

    public void Start()
    {
        if (Native.RegisterHotKey(_window.Handle, HotkeyId, Native.MOD_CONTROL | Native.MOD_NOREPEAT, Native.VK_SNAPSHOT))
        {
            _registeredOn = _window.Handle;
            return;
        }

        UsingFallback = true;
        _hookProc = HookCallback;
        _hook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _hookProc, Native.GetModuleHandle(null), 0);
    }

    /// <summary>True if neither mechanism is live - the caller should tell the user.</summary>
    public bool IsListening => _registeredOn != IntPtr.Zero || _hook != IntPtr.Zero;

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = (int)wParam;
            // Some keyboard drivers deliver PrtScn only as a key-up, so both edges are
            // accepted and the debounce in Fire() keeps that from firing twice.
            if (msg is Native.WM_KEYDOWN or Native.WM_SYSKEYDOWN or Native.WM_KEYUP or Native.WM_SYSKEYUP)
            {
                var info = System.Runtime.InteropServices.Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
                bool ctrl = (Native.GetAsyncKeyState(Native.VK_CONTROL) & 0x8000) != 0;
                if (info.vkCode == Native.VK_SNAPSHOT && ctrl)
                {
                    Fire();
                    return 1; // swallow it, so nothing else reacts to the same press
                }
            }
        }
        return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private void Fire()
    {
        var now = DateTime.UtcNow;
        if ((now - _lastFired).TotalMilliseconds < 300) return;
        _lastFired = now;
        Triggered?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_registeredOn != IntPtr.Zero)
        {
            Native.UnregisterHotKey(_registeredOn, HotkeyId);
            _registeredOn = IntPtr.Zero;
        }
        if (_hook != IntPtr.Zero)
        {
            Native.UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
        _hookProc = null;
        _window.Destroy();
    }

    /// <summary>A handle with a message loop, which is all RegisterHotKey needs.</summary>
    private sealed class MessageWindow : NativeWindow
    {
        private readonly Action _onHotkey;

        public MessageWindow(Action onHotkey)
        {
            _onHotkey = onHotkey;
            CreateHandle(new CreateParams
            {
                Caption = "YakoScreenshotHotkeyWindow",
                X = 0, Y = 0, Width = 0, Height = 0,
                Style = 0, ExStyle = 0, ClassStyle = 0,
                Parent = IntPtr.Zero,
            });
        }

        public void Destroy() => DestroyHandle();

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_HOTKEY && (int)m.WParam == HotkeyId)
                _onHotkey();
            base.WndProc(ref m);
        }
    }
}
