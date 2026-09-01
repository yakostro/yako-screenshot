namespace Yako.Screenshot;

internal static class Program
{
    // STA is required for clipboard access later on.
    [STAThread]
    private static void Main(string[] args)
    {
        // Build-time helper: regenerates app.ico from the same drawing code the tray uses.
        if (args.Length == 2 && args[0] == "--writeicon")
        {
            AppIconArt.WriteIco(args[1], 16, 24, 32, 48, 64, 128, 256);
            Console.Out.WriteLine($"wrote {args[1]}");
            Console.Out.Flush();
            return;
        }

        if (args.Contains("--selftest"))
        {
            SelfTest.Run();
            return;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // One resident instance owns the hotkey. A later launch must never just disappear -
        // that is indistinguishable from a crash, and with tray icons hidden under the
        // Windows 11 chevron there is otherwise nothing on screen to say the app is alive.
        using var single = new SingleInstance();
        if (!single.IsFirst)
        {
            var answer = MessageBox.Show(
                """
                yako-screenshot is already running.

                Press Ctrl+PrtScn at any time to capture an area.

                It has no window - it lives in the tray, and the icon may be hidden under
                the ^ arrow next to the clock. To pin it there: Taskbar settings, then
                Other system tray icons.

                Start a capture now?
                """,
                "yako-screenshot",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Information);

            if (answer == DialogResult.Yes)
                SingleInstance.PingRunningInstance();
            return;
        }

        // DPI awareness comes from app.manifest (PerMonitorV2), which the loader applies
        // before any managed code runs - earlier and more reliably than the API can.
        Application.Run(new TrayContext(single));
    }
}
