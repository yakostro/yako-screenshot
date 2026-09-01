namespace Yako.Screenshot;

/// <summary>
/// The resident half of the app: a tray icon, the hotkey, and exactly one overlay at a time.
/// </summary>
internal sealed class TrayContext : ApplicationContext
{
    private readonly Settings _settings = Settings.Load();
    private readonly NotifyIcon _icon;
    private readonly HotkeyListener _hotkey;
    private readonly Icon _trayIcon = AppIconArt.CreateIcon(Math.Max(16, SystemInformation.SmallIconSize.Width));

    private OverlayForm? _overlay;

    public TrayContext(SingleInstance single)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("Capture now", null, (_, _) => BeginCapture())
        {
            ShortcutKeyDisplayString = "Ctrl+PrtScn",
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Remove app", null, (_, _) => RemoveApp()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => ExitApp()));

        _icon = new NotifyIcon
        {
            Icon = _trayIcon,
            Text = "yako-screenshot - Ctrl+PrtScn",
            Visible = true,
            ContextMenuStrip = menu,
        };
        // A left click captures straight away - for a tray app whose only job is one
        // action, making people find the menu first is friction. Right-click still opens
        // the menu, handled by ContextMenuStrip.
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) BeginCapture();
        };

        // Do this before the first-run notice, so the notice can state it as fact.
        Autostart.Ensure(_settings);

        _hotkey = new HotkeyListener();
        _hotkey.Triggered += BeginCapture;
        _hotkey.Start();

        // A second launch of the exe asks this instance to capture - the clearest possible
        // answer to "is it even running?".
        single.Pinged += BeginCapture;
        single.Listen();

        if (!_hotkey.IsListening)
        {
            AfterStartup(() => MessageBox.Show(
                """
                Ctrl+PrtScn could not be claimed, so the hotkey will not work.

                Right-click the tray icon and choose Capture now instead.
                """,
                "yako-screenshot", MessageBoxButtons.OK, MessageBoxIcon.Warning));
        }
        else if (!_settings.FirstRunDone)
        {
            _settings.FirstRunDone = true;
            _settings.Save();
            AfterStartup(() => MessageBox.Show(
                """
                yako-screenshot is running, and will now start automatically when you
                sign in to Windows.

                Press Ctrl+PrtScn to capture an area, then Copy or Save. Left-clicking the
                tray icon does the same.

                It has no window - it lives in the tray, and the icon may be hidden under
                the ^ arrow next to the clock. To pin it there: Taskbar settings, then
                Other system tray icons. To stop it starting with Windows: Task Manager,
                then Startup apps.
                """,
                "yako-screenshot", MessageBoxButtons.OK, MessageBoxIcon.Information));
        }
    }

    /// <summary>
    /// Runs an action once the message loop is going. A dialog shown straight from the
    /// constructor would block before Application.Run ever starts pumping messages.
    /// </summary>
    private static void AfterStartup(Action action)
    {
        var timer = new System.Windows.Forms.Timer { Interval = 150 };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            timer.Dispose();
            action();
        };
        timer.Start();
    }

    private void BeginCapture()
    {
        if (_overlay is { IsDisposed: false }) return;   // one capture at a time

        CaptureResult capture;
        try
        {
            capture = ScreenCapture.Capture();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Capture failed.\n\n{ex.Message}", "yako-screenshot",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        var overlay = new OverlayForm(capture, _settings);
        _overlay = overlay;

        overlay.FormClosed += (_, _) =>
        {
            capture.Dispose();
            if (ReferenceEquals(_overlay, overlay)) _overlay = null;
            overlay.Dispose();
        };

        overlay.Show();
    }

    /// <summary>
    /// Undoes everything first launch set up: startup entry and saved settings. The program
    /// file cannot delete itself while it is running, so Explorer is opened with it selected
    /// rather than leaving the user to hunt for it.
    /// </summary>
    private void RemoveApp()
    {
        string exe = Environment.ProcessPath ?? "the program file";

        var answer = MessageBox.Show(
            $"""
            Remove yako-screenshot?

            This will stop it starting with Windows, delete its saved settings,
            and quit.

            The program file itself stays on disk. Explorer will open with it
            selected so you can delete it:

            {exe}
            """,
            "yako-screenshot",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);

        if (answer != DialogResult.Yes) return;

        Autostart.Remove();
        Settings.DeleteStore();

        try
        {
            if (File.Exists(exe))
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{exe}\"");
        }
        catch
        {
            // Not being able to open Explorer must not block the removal itself.
        }

        ExitApp();
    }

    private void ExitApp()
    {
        _overlay?.Close();
        _hotkey.Dispose();
        _icon.Visible = false;
        _icon.Dispose();
        _trayIcon.Dispose();
        ExitThread();
    }
}
