using Microsoft.Web.WebView2.Core;
using Soulcrest.App.Services;

namespace Soulcrest.App;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // Unexpected exceptions leave a trace in logs\errors.log (review 2026-10-07). On the UI thread the
        // app keeps running; on other threads .NET ends it anyway, but now with the cause written down.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
        {
            LogFile.Error("UI", e.Exception);
            if (!Services.SmokeTest.Enabled && ErrorNotice.ShouldShow(e.Exception.Message, DateTime.UtcNow))
            {
                try
                {
                    MessageBox.Show(UiText.T("Ein unerwarteter Fehler ist aufgetreten. Soulcrest läuft weiter; Details stehen in logs\\errors.log.") +
                        Environment.NewLine + Environment.NewLine + e.Exception.Message, "Soulcrest", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                finally
                {
                    ErrorNotice.Closed();
                }
            }
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            LogFile.Error(e.IsTerminating ? "Absturz" : "Hintergrund", e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString()));
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            LogFile.Error("Task", e.Exception);
            e.SetObserved();
        };
        ApplicationConfiguration.Initialize();
        _ = new SettingsService(); // load the saved language before startup messages
        Services.SmokeTest.Enabled = args.Contains("--smoke-test", StringComparer.OrdinalIgnoreCase);
        using var instance = new Mutex(false, @"Local\Soulcrest-" + Environment.UserName);
        bool acquired;
        try { acquired = instance.WaitOne(0); }
        catch (AbandonedMutexException) { acquired = true; }
        if (!acquired)
        {
            MessageBox.Show(UiText.T("Soulcrest läuft bereits."), "Soulcrest", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 1;
        }

        try
        {
            _ = CoreWebView2Environment.GetAvailableBrowserVersionString();
        }
        catch (WebView2RuntimeNotFoundException)
        {
            MessageBox.Show(
                UiText.T("Microsoft Edge WebView2 Runtime fehlt. Bitte von https://developer.microsoft.com/microsoft-edge/webview2/ installieren."),
                "Soulcrest", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 2;
        }

        using var form = new MainForm();
        if (!Services.SmokeTest.Enabled)
        {
            Application.Run(form);
            return 0;
        }

        // Invisible self-test: the window must exist for WebView2 to render, but nobody should see it.
        form.ShowInTaskbar = false;
        form.Opacity = 0;
        var exitCode = 3;
        var started = DateTime.UtcNow;
        var timer = new System.Windows.Forms.Timer { Interval = 250 };
        timer.Tick += (_, _) =>
        {
            var uiDone = Services.SmokeTest.UiRendered && Services.SmokeTest.MapShown;
            var timedOut = DateTime.UtcNow - started > TimeSpan.FromSeconds(45);
            if (!uiDone && Services.SmokeTest.Error is null && !timedOut)
                return;
            timer.Stop();
            Services.SmokeTest.Capture = "nicht gestartet (UI-Selbsttest)";
            var openCvOk = Services.SmokeTest.CheckOpenCv();
            exitCode = uiDone && openCvOk ? 0 : 3;
            Directory.CreateDirectory(Services.AppPaths.DataDirectory);
            File.WriteAllText(Path.Combine(Services.AppPaths.DataDirectory, "smoke-test.txt"),
                $"{DateTime.Now:O} exit={exitCode} {Services.SmokeTest.Describe()} after {(DateTime.UtcNow - started).TotalSeconds:0.0}s");
            form.Close();
        };
        timer.Start();
        Application.Run(form);
        return exitCode;
    }
}
