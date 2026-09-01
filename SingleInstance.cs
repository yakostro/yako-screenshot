namespace Yako.Screenshot;

/// <summary>
/// Keeps exactly one resident instance, and - just as important - makes a second launch
/// visible. Silently exiting is indistinguishable from a crash: double-clicking the exe
/// looked like nothing happened at all. Now the second launch pings the running copy, which
/// announces itself and points at the tray.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\YakoScreenshot";
    private const string WindowCaption = "YakoScreenshotSingleInstanceWindow";

    private static readonly uint PingMessage = Native.RegisterWindowMessage("YakoScreenshot.Ping");

    private readonly Mutex _mutex;
    private PingWindow? _window;

    public bool IsFirst { get; }

    /// <summary>Raised on the first instance when a later launch pings it.</summary>
    public event Action? Pinged;

    public SingleInstance()
    {
        _mutex = new Mutex(true, MutexName, out bool isFirst);
        IsFirst = isFirst;
    }

    /// <summary>Starts listening for pings. First instance only.</summary>
    public void Listen() => _window ??= new PingWindow(WindowCaption, () => Pinged?.Invoke());

    /// <summary>Called by a later launch just before it bows out.</summary>
    public static void PingRunningInstance()
    {
        if (PingMessage == 0) return;
        var hwnd = Native.FindWindow(null, WindowCaption);
        if (hwnd != IntPtr.Zero)
            Native.PostMessage(hwnd, PingMessage, IntPtr.Zero, IntPtr.Zero);
    }

    public void Dispose()
    {
        _window?.Destroy();
        _window = null;

        if (IsFirst)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Not the owning thread - nothing to release.
            }
        }
        _mutex.Dispose();
    }

    private sealed class PingWindow : NativeWindow
    {
        private readonly Action _onPing;

        public PingWindow(string caption, Action onPing)
        {
            _onPing = onPing;
            CreateHandle(new CreateParams
            {
                Caption = caption,
                X = 0, Y = 0, Width = 0, Height = 0,
                Style = 0, ExStyle = 0, ClassStyle = 0,
                Parent = IntPtr.Zero,
            });
        }

        public void Destroy() => DestroyHandle();

        protected override void WndProc(ref Message m)
        {
            if (PingMessage != 0 && (uint)m.Msg == PingMessage)
                _onPing();
            base.WndProc(ref m);
        }
    }
}
