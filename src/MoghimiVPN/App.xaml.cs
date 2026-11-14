using System.Windows;

namespace MoghimiVPN;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ProfileStore.Instance.Load();
        ProfileStore.Instance.ClearTransientConfigs();
        SystemProxyController.RecoverStaleProxy();
    }
}
