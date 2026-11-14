using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace MoghimiVPN;

public partial class App : Application
{
    private Mutex? _instanceMutex;
    private bool _ownsMutex;
    private static bool IsStartupSmokeTest =>
        Environment.GetCommandLineArgs().Any(arg =>
            string.Equals(arg, "--startup-smoke-test", StringComparison.OrdinalIgnoreCase));

    protected override void OnStartup(StartupEventArgs e)
    {
        AttachFailureHandlers();
        try
        {
            base.OnStartup(e);
            _instanceMutex = new Mutex(true, @"Local\MoghimiVPN.SingleInstance", out _ownsMutex);
            if (!_ownsMutex)
            {
                if (!IsStartupSmokeTest)
                    MessageBox.Show("برنامه مقیمی VPN از قبل در حال اجراست.", "Moghimi VPN",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown(0);
                return;
            }

            WriteStartupLog("شروع برنامه؛ نسخه " + Environment.Version + "؛ مسیر " + AppContext.BaseDirectory);
            ProfileStore.Instance.Load();
            ProfileStore.Instance.ClearTransientConfigs();
            SystemProxyController.RecoverStaleProxy();

            var window = new MainWindow();
            MainWindow = window;
            if (IsStartupSmokeTest)
            {
                var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(12) };
                timeout.Tick += (_, _) =>
                {
                    timeout.Stop();
                    WriteStartupLog("Startup smoke test timed out before MainWindow.Loaded.");
                    Shutdown(21);
                };
                window.Loaded += (_, _) =>
                {
                    timeout.Stop();
                    WriteStartupLog("Startup smoke test passed: MainWindow loaded.");
                    Shutdown(0);
                };
                timeout.Start();
            }

            window.Show();
            if (!IsStartupSmokeTest)
                WriteStartupLog("پنجره اصلی با موفقیت نمایش داده شد.");
        }
        catch (Exception ex)
        {
            WriteStartupLog("Startup failed", ex);
            if (!IsStartupSmokeTest)
            {
                try
                {
                    MessageBox.Show(
                        "برنامه نتوانست اجرا شود." + Environment.NewLine +
                        ex.Message + Environment.NewLine + Environment.NewLine +
                        "گزارش کامل خطا:" + Environment.NewLine + GetStartupLogPath(),
                        "Moghimi VPN - خطای اجرا", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                catch { }
            }
            Shutdown(1);
        }
    }

    private void AttachFailureHandlers()
    {
        DispatcherUnhandledException += (_, e) =>
        {
            WriteStartupLog("Unhandled UI exception", e.Exception);
            e.Handled = true;
            if (!IsStartupSmokeTest)
            {
                try
                {
                    MessageBox.Show(
                        "خطایی در اجرای برنامه رخ داد و برای جلوگیری از خرابی بیشتر برنامه بسته می‌شود." +
                        Environment.NewLine + e.Exception.Message + Environment.NewLine +
                        "گزارش: " + GetStartupLogPath(),
                        "Moghimi VPN - خطای برنامه", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                catch { }
            }
            Shutdown(20);
        };

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            WriteStartupLog("Unhandled process exception (terminating=" + e.IsTerminating + ")", e.ExceptionObject as Exception);

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            WriteStartupLog("Unobserved task exception", e.Exception);
            e.SetObserved();
        };
    }

    private static string GetStartupLogPath()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root)) root = Path.GetTempPath();
        return Path.Combine(root, "MoghimiVPN", "logs", "startup.log");
    }

    private static void WriteStartupLog(string message, Exception? exception = null)
    {
        try
        {
            var path = GetStartupLogPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var detail = exception is null ? string.Empty : Environment.NewLine + exception;
            File.AppendAllText(path,
                $"[{DateTimeOffset.Now:O}] {message}{detail}{Environment.NewLine}{Environment.NewLine}");
        }
        catch { }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_ownsMutex)
        {
            try { _instanceMutex?.ReleaseMutex(); } catch { }
        }
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
