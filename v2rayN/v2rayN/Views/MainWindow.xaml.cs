using System.Windows.Controls;
using System.Windows.Media;
using MaterialDesignThemes.Wpf;
using v2rayN.Base;
using v2rayN.Manager;
using v2rayN.Services;

namespace v2rayN.Views;

public partial class MainWindow
{
    private static Config _config;
    private readonly SingleReplaceableDisposable _layoutBindingsDisposable = new();
    private CheckUpdateView? _checkUpdateView;
    private BackupAndRestoreView? _backupAndRestoreView;
    private readonly MoghimiDeviceService _moghimi = MoghimiDeviceService.Instance;

    public MainWindow()
    {
        InitializeComponent();

        _config = AppManager.Instance.Config;
        ThreadPool.RegisterWaitForSingleObject(App.ProgramStarted, OnProgramStarted, null, -1, false);

        _moghimi.StatusChanged += MoghimiStatusChanged;
        _moghimi.TrafficChanged += MoghimiTrafficChanged;
        _moghimi.AccessBlocked += MoghimiAccessBlocked;
        StartMoghimiMotion();

        App.Current.SessionEnding += Current_SessionEnding;
        Closing += MainWindow_Closing;
        PreviewKeyDown += MainWindow_PreviewKeyDown;
        menuSettingsSetUWP.Click += MenuSettingsSetUWP_Click;
        menuPromotion.Click += MenuPromotion_Click;
        menuClose.Click += MenuClose_Click;
        menuCheckUpdate.Click += MenuCheckUpdate_Click;
        btnNewUpdate.Click += MenuCheckUpdate_Click;
        menuBackupAndRestore.Click += MenuBackupAndRestore_Click;

        pbTheme.Content ??= new ThemeSettingView();

        this.WhenActivated(disposables =>
        {
            //servers
            this.BindCommand(ViewModel, vm => vm.AddVmessServerCmd, v => v.menuAddVmessServer).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.AddVlessServerCmd, v => v.menuAddVlessServer).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.AddShadowsocksServerCmd, v => v.menuAddShadowsocksServer).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.AddSocksServerCmd, v => v.menuAddSocksServer).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.AddHttpServerCmd, v => v.menuAddHttpServer).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.AddTrojanServerCmd, v => v.menuAddTrojanServer).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.AddHysteria2ServerCmd, v => v.menuAddHysteria2Server).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.AddTuicServerCmd, v => v.menuAddTuicServer).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.AddWireguardServerCmd, v => v.menuAddWireguardServer).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.AddAnytlsServerCmd, v => v.menuAddAnytlsServer).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.AddNaiveServerCmd, v => v.menuAddNaiveServer).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.AddMasqueServerCmd, v => v.menuAddMasqueServer).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.AddCustomServerCmd, v => v.menuAddCustomServer).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.AddCustomOutboundServerCmd, v => v.menuAddCustomOutboundServer).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.AddPolicyGroupServerCmd, v => v.menuAddPolicyGroupServer).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.AddProxyChainServerCmd, v => v.menuAddProxyChainServer).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.AddServerViaClipboardCmd, v => v.menuAddServerViaClipboard).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.AddServerViaScanCmd, v => v.menuAddServerViaScan).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.AddServerViaImageCmd, v => v.menuAddServerViaImage).DisposeWith(disposables);

            //sub
            this.BindCommand(ViewModel, vm => vm.SubSettingCmd, v => v.menuSubSetting).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.SubUpdateCmd, v => v.menuSubUpdate).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.SubUpdateViaProxyCmd, v => v.menuSubUpdateViaProxy).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.SubGroupUpdateCmd, v => v.menuSubGroupUpdate).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.SubGroupUpdateViaProxyCmd, v => v.menuSubGroupUpdateViaProxy).DisposeWith(disposables);

            //setting
            this.BindCommand(ViewModel, vm => vm.OptionSettingCmd, v => v.menuOptionSetting).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.RoutingSettingCmd, v => v.menuRoutingSetting).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.DNSSettingCmd, v => v.menuDNSSetting).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.FullConfigTemplateCmd, v => v.menuFullConfigTemplate).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.GlobalHotkeySettingCmd, v => v.menuGlobalHotkeySetting).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.RebootAsAdminCmd, v => v.menuRebootAsAdmin).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.ClearServerStatisticsCmd, v => v.menuClearServerStatistics).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.OpenTheFileLocationCmd, v => v.menuOpenTheFileLocation).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.RegionalPresetDefaultCmd, v => v.menuRegionalPresetsDefault).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.RegionalPresetRussiaCmd, v => v.menuRegionalPresetsRussia).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.RegionalPresetIranCmd, v => v.menuRegionalPresetsIran).DisposeWith(disposables);

            this.BindCommand(ViewModel, vm => vm.ReloadCmd, v => v.menuReload).DisposeWith(disposables);
            this.OneWayBind(ViewModel, vm => vm.BlReloadEnabled, v => v.menuReload.IsEnabled).DisposeWith(disposables);

            this.OneWayBind(ViewModel, vm => vm.BlNewUpdate, v => v.btnNewUpdate.Visibility).DisposeWith(disposables);

            _layoutBindingsDisposable.DisposeWith(disposables);

            this.WhenAnyValue(v => v.ViewModel.MainGirdOrientation)
                .ObserveOn(RxSchedulers.MainThreadScheduler)
                .Subscribe(UpdateLayout)
                .DisposeWith(disposables);

            this.WhenAnyValue(v => v.ViewModel.StatusBarViewModel)
                .Subscribe(vm => ViewHost.Show(contentStatusBarView, vm))
                .DisposeWith(disposables);

            ViewModel.ReadTextFromClipboardInteraction.RegisterHandler(interaction =>
            {
                var clipboardData = WindowsUtils.GetClipboardData();
                interaction.SetOutput(clipboardData);
            }).DisposeWith(disposables);

            ViewModel.ScanScreenInteraction.RegisterHandler(interaction =>
            {
                ShowHideWindow(false);
                if (Application.Current?.MainWindow is { } window)
                {
                    var bytes = QRCodeWindowsUtils.CaptureScreen(window);
                    interaction.SetOutput(bytes);
                }
                ShowHideWindow(true);
            }).DisposeWith(disposables);

            ViewModel.BrowseImageFileInteraction.RegisterHandler(interaction =>
            {
                if (UI.OpenFileDialog(out var fileName, "PNG|*.png|All|*.*") != true)
                {
                    interaction.SetOutput(null);
                    return;
                }
                interaction.SetOutput(fileName);
            }).DisposeWith(disposables);

            ViewModel.ShowHideWindowInteraction.RegisterHandler(interaction =>
            {
                ShowHideWindow(interaction.Input);
                interaction.SetOutput(RxVoid.Default);
            }).DisposeWith(disposables);

            AppEvents.SendSnackMsgRequested
              .AsObservable()
              .ObserveOn(RxSchedulers.MainThreadScheduler)
              .Subscribe(async content => await DelegateSnackMsg(content))
              .DisposeWith(disposables);

            AppEvents.AppExitRequested
              .AsObservable()
              .ObserveOn(RxSchedulers.MainThreadScheduler)
              .Subscribe(_ => StorageUI())
              .DisposeWith(disposables);

            AppEvents.ShutdownRequested
             .AsObservable()
             .ObserveOn(RxSchedulers.MainThreadScheduler)
             .Subscribe(Shutdown)
             .DisposeWith(disposables);
        });

        Title = $"{Utils.GetVersion()} - {(Utils.IsAdministrator() ? ResUI.RunAsAdmin : ResUI.NotRunAsAdmin)}";
        if (_config.UiItem.AutoHideStartup)
        {
            WindowState = WindowState.Minimized;
        }

        if (!_config.GuiItem.EnableHWA)
        {
            RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        }

        AddHelpMenuItem();
        WindowsManager.Instance.RegisterGlobalHotkey(_config, OnHotkeyHandler, null);
    }

    #region Event

    private void OnProgramStarted(object state, bool timeout)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            ShowHideWindow(true);
        });
    }

    private async Task DelegateSnackMsg(string content)
    {
        MainSnackbar.MessageQueue?.Enqueue(content);
        await Task.CompletedTask;
    }

    private void OnHotkeyHandler(EGlobalHotkey e)
    {
        switch (e)
        {
            case EGlobalHotkey.ShowForm:
                ShowHideWindow(null);
                break;

            case EGlobalHotkey.SystemProxyClear:
            case EGlobalHotkey.SystemProxySet:
            case EGlobalHotkey.SystemProxyUnchanged:
            case EGlobalHotkey.SystemProxyPac:
                AppEvents.SysProxyChangeRequested.Publish((ESysProxyType)((int)e - 1));
                break;
        }
    }

    private void StartMoghimiMotion()
    {
        MoghimiOrbitRotation.BeginAnimation(
            System.Windows.Media.RotateTransform.AngleProperty,
            new System.Windows.Media.Animation.DoubleAnimation(0, 360, TimeSpan.FromSeconds(16))
            {
                RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever
            });
        MoghimiGlow.BeginAnimation(
            UIElement.OpacityProperty,
            new System.Windows.Media.Animation.DoubleAnimation(0.48, 0.9, TimeSpan.FromSeconds(2.4))
            {
                AutoReverse = true,
                RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever,
                EasingFunction = new System.Windows.Media.Animation.SineEase
                {
                    EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut
                }
            });
        _ = RefreshMoghimiStatusAsync();
    }

    private async Task RefreshMoghimiStatusAsync()
    {
        try
        {
            var status = await _moghimi.GetStatusAsync(forceRefresh: true);
            MoghimiStatusChanged(status);
            SetMoghimiVisualState(
                ViewModel.StatusBarViewModel.SystemProxySelected == (int)ESysProxyType.ForcedChange ||
                ViewModel.StatusBarViewModel.EnableTun);
        }
        catch (Exception ex)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                MoghimiConnectionStateText.Text = "سرویس سهمیه در دسترس نیست";
                AppendMoghimiUiLog(ex.Message);
            }));
        }
    }

    private void MoghimiStatusChanged(MoghimiQuotaStatus status)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            var freeLimit = Math.Max(1, status.FreeLimitBytes);
            var remaining = Math.Clamp(status.FreeRemainingBytes, 0, freeLimit);
            var used = Math.Max(0, status.FreeUsedBytes);
            MoghimiFreeQuotaProgress.Value = Math.Clamp(100d * used / freeLimit, 0d, 100d);
            MoghimiQuotaSummaryText.Text =
                $"{FormatMoghimiBytes(remaining)} از {FormatMoghimiBytes(freeLimit)} باقی‌مانده";
            MoghimiPaidQuotaSummaryText.Text = status.PaidRemainingBytes > 0
                ? $"حجم اشتراک: {FormatMoghimiBytes(status.PaidRemainingBytes)} باقی‌مانده"
                : "حجم خریداری‌شده: فعال نیست";
            MoghimiQuotaMonthText.Text = string.IsNullOrWhiteSpace(status.FreeMonthStart)
                ? "بازنشانی ماهانه بر اساس UTC"
                : $"شروع دوره UTC: {status.FreeMonthStart}";
            MoghimiDeviceIdText.Text = $"DEVICE  /  {_moghimi.DeviceDisplayId}";
            if (!status.AccessAllowed)
                MoghimiConnectionStateText.Text = "سهمیه قابل استفاده تمام شده";
            else if (status.PaidRemainingBytes > 0 && status.FreeRemainingBytes <= 0)
                MoghimiConnectionStateText.Text = "اشتراک خریداری‌شده فعال است";
            else
                MoghimiConnectionStateText.Text = "سهمیه رایگان ویندوز فعال است";
        }));
    }

    private void MoghimiTrafficChanged(long downloadBytes, long uploadBytes)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            MoghimiTrafficDownText.Text = FormatMoghimiBytes(downloadBytes);
            MoghimiTrafficUpText.Text = FormatMoghimiBytes(uploadBytes);
        }));
    }

    private void MoghimiAccessBlocked(string message)
    {
        Dispatcher.BeginInvoke(new Action(async () =>
        {
            try
            {
                ViewModel.StatusBarViewModel.SystemProxySelected = (int)ESysProxyType.ForcedClear;
                ViewModel.StatusBarViewModel.EnableTun = false;
                await CoreManager.Instance.CoreStop();
                SetMoghimiVisualState(false);
                MoghimiConnectionStateText.Text = "اتصال متوقف شد · سهمیه کافی نیست";
                MessageBox.Show(message, "وضعیت سهمیه مقیمی", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                AppendMoghimiUiLog(ex.Message);
            }
        }));
    }

    private async void MoghimiSyncButton_Click(object sender, RoutedEventArgs e)
    {
        MoghimiSyncButton.IsEnabled = false;
        try
        {
            if (!await _moghimi.EnsureAccessAsync(CancellationToken.None, forceRefresh: true))
                throw new InvalidOperationException("سهمیه یا اشتراک این دستگاه اجازه همگام‌سازی نمی‌دهد.");

            var count = await _moghimi.SyncCatalogAsync();
            await ViewModel.ProfilesViewModel.RefreshSubscriptions();
            var cloudGroup = ViewModel.ProfilesViewModel.SubItems
                .FirstOrDefault(item => item.Id == MoghimiDeviceService.SubscriptionId);
            if (cloudGroup is not null)
            {
                ViewModel.ProfilesViewModel.SelectedSub = cloudGroup;
                await ViewModel.ProfilesViewModel.RefreshServers();
            }

            MoghimiConnectionStateText.Text = $"همگام‌سازی تکمیل شد · {count} سرور";
            MessageBox.Show($"{count} کانفیگ از فهرست سرورهای مقیمی دریافت و به v2rayN اضافه شد.",
                "همگام‌سازی موفق", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MoghimiConnectionStateText.Text = "همگام‌سازی ناموفق بود";
            MessageBox.Show(ex.Message, "خطا در همگام‌سازی", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            MoghimiSyncButton.IsEnabled = true;
        }
    }

    private async void MoghimiActivateButton_Click(object sender, RoutedEventArgs e)
    {
        MoghimiActivateButton.IsEnabled = false;
        try
        {
            await _moghimi.ActivateSubscriptionAsync(MoghimiActivationCodeInput.Text);
            MoghimiActivationCodeInput.Clear();
            await RefreshMoghimiStatusAsync();
            MessageBox.Show("کد فعال‌سازی برای همین دستگاه ثبت شد.", "فعال‌سازی موفق",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "فعال‌سازی ناموفق بود", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            MoghimiActivateButton.IsEnabled = true;
        }
    }

    private async void MoghimiConnectButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var statusBar = ViewModel.StatusBarViewModel;
            var currentlyConnected =
                statusBar.SystemProxySelected == (int)ESysProxyType.ForcedChange || statusBar.EnableTun;

            if (currentlyConnected)
            {
                statusBar.SystemProxySelected = (int)ESysProxyType.ForcedClear;
                statusBar.EnableTun = false;
                SetMoghimiVisualState(false);
                return;
            }

            if (!await _moghimi.EnsureAccessAsync(CancellationToken.None, forceRefresh: true))
                throw new InvalidOperationException("سهمیه ۸ گیگابایتی و حجم اشتراک خریداری‌شده تمام شده است.");

            statusBar.SystemProxySelected = (int)ESysProxyType.ForcedChange;
            SetMoghimiVisualState(true);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "اتصال مقیمی برقرار نشد", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void SetMoghimiVisualState(bool connected)
    {
        MoghimiConnectButton.Content = connected ? "قطع اتصال" : "اتصال امن";
        MoghimiConnectionStateText.Text = connected
            ? "تونل فعال است · ترافیک تحت محاسبه"
            : "آماده اتصال امن";
        var duration = connected ? TimeSpan.FromSeconds(4.5) : TimeSpan.FromSeconds(16);
        MoghimiOrbitRotation.BeginAnimation(
            System.Windows.Media.RotateTransform.AngleProperty,
            new System.Windows.Media.Animation.DoubleAnimation(0, 360, duration)
            {
                RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever
            });
    }

    private static string FormatMoghimiBytes(long bytes)
    {
        var value = Math.Max(0, bytes);
        const double kib = 1024d;
        const double mib = 1024d * 1024d;
        const double gib = 1024d * 1024d * 1024d;
        return value >= gib ? $"{value / gib:0.00} GiB"
            : value >= mib ? $"{value / mib:0.0} MiB"
            : value >= kib ? $"{value / kib:0} KiB"
            : $"{value} B";
    }

    private void AppendMoghimiUiLog(string message)
    {
        Logging.SaveLog("MoghimiDashboard", new InvalidOperationException(message));
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        e.Cancel = true;
        ShowHideWindow(false);
    }

    private async void Current_SessionEnding(object sender, SessionEndingCancelEventArgs e)
    {
        Logging.SaveLog("Current_SessionEnding");
        StorageUI();
        await AppManager.Instance.AppExitAsync(false);
    }

    private void Shutdown(bool obj)
    {
        Application.Current.Shutdown();
    }

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl))
        {
            switch (e.Key)
            {
                case Key.V:
                    if (Keyboard.FocusedElement is TextBox)
                    {
                        return;
                    }
                    AddServerViaClipboardAsync().ContinueWith(_ => { });

                    break;

                case Key.S:
                    ScanScreenTaskAsync().ContinueWith(_ => { });
                    break;
            }
        }
        else
        {
            if (e.Key == Key.F5)
            {
                ViewModel?.Reload();
            }
        }
    }

    private void MenuClose_Click(object sender, RoutedEventArgs e)
    {
        StorageUI();
        ShowHideWindow(false);
    }

    private void MenuPromotion_Click(object sender, RoutedEventArgs e)
    {
        ProcUtils.ProcessStart($"{Utils.Base64Decode(Global.PromotionUrl)}?t={DateTime.Now.Ticks}");
    }

    private void MenuSettingsSetUWP_Click(object sender, RoutedEventArgs e)
    {
        ProcUtils.ProcessStart(Utils.GetBinPath("EnableLoopback.exe"));
    }

    public async Task AddServerViaClipboardAsync()
    {
        var clipboardData = WindowsUtils.GetClipboardData();
        if (clipboardData.IsNotEmpty() && ViewModel != null)
        {
            await ViewModel.AddServerViaClipboardAsync(clipboardData);
        }
    }

    private async Task ScanScreenTaskAsync()
    {
        ShowHideWindow(false);

        if (Application.Current?.MainWindow is Window window)
        {
            var bytes = QRCodeWindowsUtils.CaptureScreen(window);
            await ViewModel?.ScanScreenResult(bytes);
        }

        ShowHideWindow(true);
    }

    private void MenuCheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        _checkUpdateView ??= new CheckUpdateView();
        _checkUpdateView.ViewModel = ViewModel?.CheckUpdateViewModel;
        DialogHost.Show(_checkUpdateView, "RootDialog");

        AppEvents.HasUpdateNotified.Publish(false);
    }

    private void MenuBackupAndRestore_Click(object sender, RoutedEventArgs e)
    {
        _backupAndRestoreView ??= new BackupAndRestoreView();
        _backupAndRestoreView.ViewModel = ViewModel?.BackupAndRestoreViewModel;
        DialogHost.Show(_backupAndRestoreView, "RootDialog");
    }

    #endregion Event

    #region UI

    public void ShowHideWindow(bool? blShow)
    {
        var bl = blShow ?? !AppManager.Instance.ShowInTaskbar;
        if (bl)
        {
            this?.Show();
            if (this?.WindowState == WindowState.Minimized)
            {
                WindowState = WindowState.Normal;
            }
            this?.Activate();
            this?.Focus();
        }
        else
        {
            this?.Hide();
        }
        AppManager.Instance.ShowInTaskbar = bl;
    }

    protected override void OnLoaded(object? sender, RoutedEventArgs e)
    {
        base.OnLoaded(sender, e);
        if (_config.UiItem.AutoHideStartup)
        {
            ShowHideWindow(false);
        }
        RestoreUI();
    }

    private void RestoreUI()
    {
        if (_config.UiItem.MainGirdHeight1 > 0 && _config.UiItem.MainGirdHeight2 > 0)
        {
            if (_config.UiItem.MainGirdOrientation == EGirdOrientation.Horizontal)
            {
                gridMain.ColumnDefinitions[0].Width = new GridLength(_config.UiItem.MainGirdHeight1, GridUnitType.Star);
                gridMain.ColumnDefinitions[2].Width = new GridLength(_config.UiItem.MainGirdHeight2, GridUnitType.Star);
            }
            else if (_config.UiItem.MainGirdOrientation == EGirdOrientation.Vertical)
            {
                gridMain1.RowDefinitions[0].Height = new GridLength(_config.UiItem.MainGirdHeight1, GridUnitType.Star);
                gridMain1.RowDefinitions[2].Height = new GridLength(_config.UiItem.MainGirdHeight2, GridUnitType.Star);
            }
        }
    }

    private void StorageUI()
    {
        ConfigHandler.SaveWindowSizeItem(_config, GetType().Name, Width, Height);

        if (_config.UiItem.MainGirdOrientation == EGirdOrientation.Horizontal)
        {
            ConfigHandler.SaveMainGirdHeight(_config, gridMain.ColumnDefinitions[0].ActualWidth, gridMain.ColumnDefinitions[2].ActualWidth);
        }
        else if (_config.UiItem.MainGirdOrientation == EGirdOrientation.Vertical)
        {
            ConfigHandler.SaveMainGirdHeight(_config, gridMain1.RowDefinitions[0].ActualHeight, gridMain1.RowDefinitions[2].ActualHeight);
        }
    }

    private void UpdateLayout(EGirdOrientation orientation)
    {
        var currentLayoutDisposables = new MultipleDisposable();
        _layoutBindingsDisposable.Create(currentLayoutDisposables);

        gridMain.Visibility = orientation == EGirdOrientation.Horizontal ? Visibility.Visible : Visibility.Collapsed;
        gridMain1.Visibility = orientation == EGirdOrientation.Vertical ? Visibility.Visible : Visibility.Collapsed;
        gridMain2.Visibility = orientation == EGirdOrientation.Tab ? Visibility.Visible : Visibility.Collapsed;

        switch (orientation)
        {
            case EGirdOrientation.Horizontal:
                this.WhenAnyValue(v => v.ViewModel.ProfilesViewModel)
                    .Subscribe(vm => ViewHost.Show(tabProfiles, vm))
                    .DisposeWith(currentLayoutDisposables);
                this.WhenAnyValue(v => v.ViewModel.MsgViewModel)
                    .Subscribe(vm => ViewHost.Show(tabMsgView, vm))
                    .DisposeWith(currentLayoutDisposables);
                this.WhenAnyValue(v => v.ViewModel.ClashProxiesViewModel)
                    .Subscribe(vm => ViewHost.Show(tabClashProxies, vm))
                    .DisposeWith(currentLayoutDisposables);
                this.WhenAnyValue(v => v.ViewModel.ClashConnectionsViewModel)
                    .Subscribe(vm => ViewHost.Show(tabClashConnections, vm))
                    .DisposeWith(currentLayoutDisposables);
                this.OneWayBind(ViewModel, vm => vm.ShowClashUI, v => v.tabMsgView.Visibility).DisposeWith(currentLayoutDisposables);
                this.OneWayBind(ViewModel, vm => vm.ShowClashUI, v => v.tabClashProxies.Visibility).DisposeWith(currentLayoutDisposables);
                this.OneWayBind(ViewModel, vm => vm.ShowClashUI, v => v.tabClashConnections.Visibility).DisposeWith(currentLayoutDisposables);
                this.Bind(ViewModel, vm => vm.TabMainSelectedIndex, v => v.tabMain.SelectedIndex).DisposeWith(currentLayoutDisposables);
                break;

            case EGirdOrientation.Vertical:
                this.WhenAnyValue(v => v.ViewModel.ProfilesViewModel)
                    .Subscribe(vm => ViewHost.Show(tabProfiles1, vm))
                    .DisposeWith(currentLayoutDisposables);
                this.WhenAnyValue(v => v.ViewModel.MsgViewModel)
                    .Subscribe(vm => ViewHost.Show(tabMsgView1, vm))
                    .DisposeWith(currentLayoutDisposables);
                this.WhenAnyValue(v => v.ViewModel.ClashProxiesViewModel)
                    .Subscribe(vm => ViewHost.Show(tabClashProxies1, vm))
                    .DisposeWith(currentLayoutDisposables);
                this.WhenAnyValue(v => v.ViewModel.ClashConnectionsViewModel)
                    .Subscribe(vm => ViewHost.Show(tabClashConnections1, vm))
                    .DisposeWith(currentLayoutDisposables);
                this.OneWayBind(ViewModel, vm => vm.ShowClashUI, v => v.tabMsgView1.Visibility).DisposeWith(currentLayoutDisposables);
                this.OneWayBind(ViewModel, vm => vm.ShowClashUI, v => v.tabClashProxies1.Visibility).DisposeWith(currentLayoutDisposables);
                this.OneWayBind(ViewModel, vm => vm.ShowClashUI, v => v.tabClashConnections1.Visibility).DisposeWith(currentLayoutDisposables);
                this.Bind(ViewModel, vm => vm.TabMainSelectedIndex, v => v.tabMain1.SelectedIndex).DisposeWith(currentLayoutDisposables);
                break;

            case EGirdOrientation.Tab:
            default:
                this.WhenAnyValue(v => v.ViewModel.ProfilesViewModel)
                    .Subscribe(vm => ViewHost.Show(tabProfiles2, vm))
                    .DisposeWith(currentLayoutDisposables);
                this.WhenAnyValue(v => v.ViewModel.MsgViewModel)
                    .Subscribe(vm => ViewHost.Show(tabMsgView2, vm))
                    .DisposeWith(currentLayoutDisposables);
                this.WhenAnyValue(v => v.ViewModel.ClashProxiesViewModel)
                    .Subscribe(vm => ViewHost.Show(tabClashProxies2, vm))
                    .DisposeWith(currentLayoutDisposables);
                this.WhenAnyValue(v => v.ViewModel.ClashConnectionsViewModel)
                    .Subscribe(vm => ViewHost.Show(tabClashConnections2, vm))
                    .DisposeWith(currentLayoutDisposables);
                this.OneWayBind(ViewModel, vm => vm.ShowClashUI, v => v.tabClashProxies2.Visibility).DisposeWith(currentLayoutDisposables);
                this.OneWayBind(ViewModel, vm => vm.ShowClashUI, v => v.tabClashConnections2.Visibility).DisposeWith(currentLayoutDisposables);
                this.Bind(ViewModel, vm => vm.TabMainSelectedIndex, v => v.tabMain2.SelectedIndex).DisposeWith(currentLayoutDisposables);
                break;
        }

        RestoreUI();
    }

    private void AddHelpMenuItem()
    {
        var coreInfo = CoreInfoManager.Instance.GetCoreInfo();
        foreach (var it in coreInfo
            .Where(t => t.CoreType is not ECoreType.v2fly
                        and not ECoreType.hysteria))
        {
            var item = new MenuItem()
            {
                Tag = it.Url.Replace(@"/releases", ""),
                Header = string.Format(ResUI.menuWebsiteItem, it.CoreType.ToString().Replace("_", " ")).UpperFirstChar()
            };
            item.Click += MenuItem_Click;
            menuHelp.Items.Add(item);
        }
    }

    private void MenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem item)
        {
            ProcUtils.ProcessStart(item.Tag.ToString());
        }
    }

    #endregion UI
}
