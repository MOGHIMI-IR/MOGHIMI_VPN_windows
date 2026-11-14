using System.Threading;
using System.Windows;

namespace MoghimiVPN;

public partial class App : Application
{
    private Mutex? _instanceMutex;
    private bool _ownsMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        _instanceMutex = new Mutex(true, @"Local\MoghimiVPN.SingleInstance", out _ownsMutex);
        if (!_ownsMutex)
        {
            MessageBox.Show("برنامه مقیمی VPN از قبل در حال اجراست.", "Moghimi VPN",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        base.OnStartup(e);
        ProfileStore.Instance.Load();
        ProfileStore.Instance.ClearTransientConfigs();
        SystemProxyController.RecoverStaleProxy();
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
