using Microsoft.Win32;
using System.Net.NetworkInformation;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace MoghimiVPN;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly ConnectionManager _connection = new();
    private readonly WindowsLicenseService _license = new();
    private readonly CloudSync _cloudSync;
    private readonly SubscriptionService _subscriptions = new();
    private readonly DispatcherTimer _logTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly DispatcherTimer _cloudTimer = new() { Interval = TimeSpan.FromMinutes(15) };
    private readonly DispatcherTimer _sessionTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _usageTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private Guid _usageSessionId;
    private long _usageSequence;
    private long _usageReportedDownloadBytes;
    private long _usageReportedUploadBytes;
    private readonly SemaphoreSlim _usageReportGate = new(1, 1);
    private DateTimeOffset? _connectedSince;
    private bool _cloudSyncRunning;
    private bool _connectionBusy;
    private CancellationTokenSource? _connectScanCts;
    private CancellationTokenSource? _manualScanCts;
    private bool _manualScanBusy;
    private bool _subscriptionRefreshRunning;
    private bool _closeAfterBusyOperation;
    private ProxyProfile? _selectedProfile;
    private ProxyProfile? _connectedProfile;
    private SubscriptionItem? _selectedSubscription;
    private bool _isConnected;
    private bool _pendingEngineExit;
    private bool _reconnectInProgress;
    private bool _windowsUpdateCheckRunning;
    private bool _mandatoryWindowsUpdate;
    private WindowsReleaseInfo? _availableWindowsRelease;

    public ObservableCollection<ProxyProfile> Profiles { get; } = [];
    public ObservableCollection<SubscriptionItem> Subscriptions { get; } = [];
    public event PropertyChangedEventHandler? PropertyChanged;

    public MainWindow()
    {
        InitializeComponent();
        _cloudSync = new CloudSync(_license);
        DataContext = this;
        ProfileStore.Instance.State.Profiles = ProfileStore.Instance.State.Profiles
            .Where(p => p.SubscriptionId != "moghimi-cloud")
            .ToList();
        foreach (var p in ProfileStore.Instance.State.Profiles) Profiles.Add(p);
        foreach (var s in ProfileStore.Instance.State.Subscriptions) Subscriptions.Add(s);
        ProfileStore.Instance.Save();
        var active = Profiles.FirstOrDefault(p => p.Id == ProfileStore.Instance.State.ActiveProfileId) ?? Profiles.FirstOrDefault();
        if (active is not null) ProfilesList.SelectedItem = active;
        LoadSettings();
        UpdateDashboard();
        _connection.EngineExited += (_, _) => Dispatcher.BeginInvoke(new Action(() =>
        {
            // Ignore a delayed exit notification if a later retry already started the engine.
            if (_connection.IsRunning) return;
            if (_connectionBusy)
            {
                _pendingEngineExit = true;
                return;
            }
            _ = HandleUnexpectedEngineExitAsync();
        }));
        _connection.LogMessage += (_, message) => Dispatcher.Invoke(() => AppendLog(message));
        _logTimer.Tick += (_, _) => { if (Pages.SelectedIndex == 4) RefreshLogs(); };
        _sessionTimer.Tick += (_, _) => UpdateSessionClock();
        _usageTimer.Tick += async (_, _) => await ReportWindowsUsageAsync(silent: true);
        var startupSmokeTest = Environment.GetCommandLineArgs().Any(argument =>
            string.Equals(argument, "--startup-smoke-test", StringComparison.OrdinalIgnoreCase));
        if (!startupSmokeTest)
        {
            _logTimer.Start();
            _cloudTimer.Tick += async (_, _) => await SyncCloudAsync(silent: true);
            _cloudTimer.Start();
            Loaded += async (_, _) =>
            {
                await RefreshWindowsLicenseStatusAsync(silent: true);
                var entitlement = await _license.GetStatusAsync();
                UpdateWindowsLicenseUi(entitlement);
                if (entitlement.Active) await SyncCloudAsync(silent: true);
                await CheckWindowsUpdateAsync(silent: true);
            };
        }
    }

    private void Pages_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.OriginalSource != Pages) return;
        if (Pages.SelectedContent is not UIElement content) return;

        content.BeginAnimation(UIElement.OpacityProperty, null);
        content.Opacity = 0.72;
        content.BeginAnimation(UIElement.OpacityProperty,
            new System.Windows.Media.Animation.DoubleAnimation
            {
                From = 0.72,
                To = 1.0,
                Duration = TimeSpan.FromMilliseconds(190),
                EasingFunction = new System.Windows.Media.Animation.QuadraticEase
                {
                    EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut
                }
            });
    }

    private void HomeNav_Click(object sender, RoutedEventArgs e) => Navigate(0, "داشبورد اتصال", "وضعیت اتصال و سرور فعال");
    private void ProfilesNav_Click(object sender, RoutedEventArgs e) => Navigate(1, "مدیریت کانفیگ‌ها", "سرورها را اضافه، انتخاب و تست کن");
    private void SubscriptionsNav_Click(object sender, RoutedEventArgs e) => Navigate(2, "اشتراک‌ها", "دریافت کانفیگ‌های اشتراکی و سایت مقیمی");
    private void SettingsNav_Click(object sender, RoutedEventArgs e) => Navigate(3, "تنظیمات شبکه", "حالت تونل، DNS و حریم خصوصی");
    private void LogsNav_Click(object sender, RoutedEventArgs e) => Navigate(4, "گزارش اتصال", "گزارش‌های محلی برای عیب‌یابی");

    private void Navigate(int index, string title, string description)
    {
        Pages.SelectedIndex = index;
        PageTitle.Text = title;
        PageDescription.Text = description;
    }

    private void LoadSettings()
    {
        var s = ProfileStore.Instance.State.Settings;
        ConnectionModeCombo.SelectedIndex = s.ConnectionMode == "System Proxy" ? 1 : 0;
        DnsTextBox.Text = s.DnsServer;
        BlockAdsCheck.IsChecked = s.BlockAds;
        BypassLanCheck.IsChecked = s.BypassLan;
    }

    private void SaveSettingsFromUI()
    {
        var s = ProfileStore.Instance.State.Settings;
        s.ConnectionMode = (ConnectionModeCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "TUN";
        s.DnsServer = string.IsNullOrWhiteSpace(DnsTextBox.Text) ? "1.1.1.1" : DnsTextBox.Text.Trim();
        s.BlockAds = BlockAdsCheck.IsChecked == true;
        s.BypassLan = BypassLanCheck.IsChecked == true;
        ProfileStore.Instance.Save();
        UpdateDashboard();
    }

    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        if (_connection.IsRunning || _connectionBusy)
        {
            MessageBox.Show("برای تغییر DNS، مسیریابی یا حالت VPN/Proxy ابتدا اتصال را قطع کن. تغییرات فقط در اتصال بعدی اعمال می‌شوند.",
                "تنظیمات در حال استفاده", MessageBoxButton.OK, MessageBoxImage.Information);
            LoadSettings();
            return;
        }
        SaveSettingsFromUI();
        MessageBox.Show("تنظیمات ذخیره شد.", "Moghimi VPN", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ProfilesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selectedProfile = ProfilesList.SelectedItem as ProxyProfile;
        ProfileStore.Instance.State.Profiles = Profiles
            .Where(p => p.SubscriptionId != "moghimi-cloud")
            .ToList();
        if (_selectedProfile is not null)
            ProfileStore.Instance.State.ActiveProfileId = _selectedProfile.Id;
        UpdateDashboard();
        ProfileStore.Instance.Save();
    }

    private void SubscriptionsList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        _selectedSubscription = SubscriptionsList.SelectedItem as SubscriptionItem;

    private void UpdateDashboard()
    {
        var selected = (_connection.IsRunning ? _connectedProfile : null)
            ?? _selectedProfile
            ?? Profiles.FirstOrDefault();
        SelectedProfileName.Text = selected?.Name ?? "هنوز سروری انتخاب نشده";
        SelectedProfileEndpoint.Text = selected is null ? "از بخش کانفیگ‌ها یک سرور اضافه کن" : $"{selected.Protocol.ToUpperInvariant()}  ·  {selected.Server}:{selected.Port}";
        SelectedProfileLatency.Text = selected is null || selected.LatencyMs < 0 ? "پینگ آزمایش نشده" : $"{selected.LatencyMs} ms";
        ProfileCountSummary.Text = Profiles.Count.ToString();
        DnsSummary.Text = ProfileStore.Instance.State.Settings.DnsServer;
        ModeSummary.Text = ProfileStore.Instance.State.Settings.ConnectionMode == "TUN" ? "VPN Mode" : "Proxy Mode";
        ConnectionStatus.Text = _isConnected
            ? (ProfileStore.Instance.State.Settings.ConnectionMode == "TUN" ? "VPN فعال" : "Proxy فعال")
            : "قطع";
        ConnectButton.Content = _isConnected ? "قطع اتصال" : "اتصال امن";
        HeaderStatus.Text = _isConnected ? "متصل" : "آماده اتصال";
        StatusDot.Fill = _isConnected ? System.Windows.Media.Brushes.LimeGreen : System.Windows.Media.Brushes.Goldenrod;
        OnPropertyChanged(nameof(Profiles));
    }

    private void UpdateSessionClock()
    {
        if (SessionUptime is null) return;
        if (!_isConnected || _connectedSince is null)
        {
            SessionUptime.Text = "00:00:00";
            SessionUptime.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(141, 152, 169));
            if (TrafficDownloadText is not null) TrafficDownloadText.Text = "0 KB";
            if (TrafficUploadText is not null) TrafficUploadText.Text = "0 KB";
            return;
        }

        var elapsed = DateTimeOffset.Now - _connectedSince.Value;
        SessionUptime.Text = elapsed.TotalDays >= 1
            ? $"{(int)elapsed.TotalDays:00}:{elapsed.Hours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}"
            : $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";
        SessionUptime.Foreground = System.Windows.Media.Brushes.LightGreen;
        var traffic = _connection.GetTrafficSnapshot();
        if (TrafficDownloadText is not null) TrafficDownloadText.Text = FormatTraffic(traffic.DownloadBytes);
        if (TrafficUploadText is not null) TrafficUploadText.Text = FormatTraffic(traffic.UploadBytes);
    }

    private static string FormatTraffic(long bytes)
    {
        var value = Math.Max(0, bytes);
        const double kib = 1024d, mib = 1024d * 1024d, gib = 1024d * 1024d * 1024d;
        return value >= gib ? $"{value / gib:0.00} GB"
            : value >= mib ? $"{value / mib:0.0} MB"
            : value >= kib ? $"{value / kib:0} KB"
            : $"{value} B";
    }

    private void SetDisconnected(string message)
    {
        _isConnected = false;
        _connectedProfile = null;
        _connectedSince = null;
        _usageTimer.Stop();
        _usageSessionId = Guid.Empty;
        _usageSequence = 0;
        _sessionTimer.Stop();
        UpdateSessionClock();
        ConnectionStatus.Text = message;
        ProbeStatus.Text = "سرور انتخاب‌شده آماده اتصال";
        ProbeProgress.Visibility = Visibility.Collapsed;
        ConnectButton.Content = "اتصال امن";
        HeaderStatus.Text = "قطع";
        StopOrbAnimation();
        StatusDot.Fill = System.Windows.Media.Brushes.Goldenrod;
        RefreshLogs();
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_mandatoryWindowsUpdate)
        {
            MessageBox.Show("برای ادامه استفاده از برنامه، ابتدا نسخه جدید ویندوز را نصب کن.",
                "به‌روزرسانی اجباری", MessageBoxButton.OK, MessageBoxImage.Warning);
            Navigate(3, "تنظیمات شبکه", "به‌روزرسانی برنامه");
            return;
        }
        if (_subscriptionRefreshRunning)
        {
            MessageBox.Show("به‌روزرسانی اشتراک هنوز تمام نشده؛ بعد از پایان آن اتصال را بزن.", "Moghimi VPN", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (_manualScanBusy)
        {
            MessageBox.Show("تست دستی تأخیر در حال اجراست؛ بعد از پایان آن اتصال را شروع کن.", "Moghimi VPN", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (_connectionBusy)
        {
            _connectScanCts?.Cancel();
            if (_connectScanCts is not null) ProbeStatus.Text = "درخواست توقف اتصال ثبت شد…";
            return;
        }
        if (_cloudSyncRunning)
        {
            MessageBox.Show("همگام‌سازی کانفیگ‌های سایت هنوز تمام نشده؛ بعد از پایان دریافت دوباره اتصال را بزن.", "Moghimi VPN", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (_connection.IsRunning)
        {
            _connectionBusy = true;
            ConnectButton.IsEnabled = false;
            ConnectionStatus.Text = "در حال قطع اتصال…";
            HeaderStatus.Text = "در حال قطع";
            ProbeStatus.Text = "در حال پاک‌سازی مسیر شبکه";
            try
            {
                await ReportWindowsUsageAsync(silent: true);
                _usageTimer.Stop();
                await _connection.StopAsync();
                _connectedProfile = null;
                _isConnected = false;
                SetDisconnected("اتصال قطع شد");
                UpdateDashboard();
            }
            catch (Exception ex)
            {
                AppendLog("Disconnect failed: " + ex.Message);
                MessageBox.Show(ex.Message, "قطع اتصال ناموفق بود", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                _connectionBusy = false;
                ConnectButton.IsEnabled = true;
                if (_closeAfterBusyOperation) { _closeAfterBusyOperation = false; Close(); }
            }
            return;
        }
        if (Profiles.Count == 0)
        {
            MessageBox.Show("اول یک کانفیگ اضافه کن یا کانفیگ‌های سایت مقیمی را همگام‌سازی کن.", "Moghimi VPN", MessageBoxButton.OK, MessageBoxImage.Information);
            Navigate(1, "مدیریت کانفیگ‌ها", "سرور موردنظرت را خودت انتخاب کن");
            return;
        }

        // Manual client flow: start only the profile selected by the user.
        var chosen = ProfilesList.SelectedItem as ProxyProfile ?? _selectedProfile ?? Profiles.FirstOrDefault();
        if (chosen is null)
        {
            MessageBox.Show("یک سرور را از فهرست کانفیگ‌ها انتخاب کن.", "Moghimi VPN", MessageBoxButton.OK, MessageBoxImage.Information);
            Navigate(1, "مدیریت کانفیگ‌ها", "یک سرور را انتخاب کن");
            return;
        }
        _selectedProfile = chosen;
        ProfilesList.SelectedItem = chosen;
        try
        {
            var entitlement = await _license.GetStatusAsync(forceRefresh: true);
            UpdateWindowsLicenseUi(entitlement);
            if (!entitlement.Active)
            {
                MessageBox.Show(
                    "سهمیه رایگان ۷ گیبی‌بایتی ویندوز و حجم خریداری‌شده قابل استفاده تمام شده است. سهمیه رایگان در شروع ماه UTC بعدی شارژ می‌شود.",
                    "سهمیه اتصال تمام شده", MessageBoxButton.OK, MessageBoxImage.Information);
                Navigate(3, "تنظیمات شبکه", "وضعیت سهمیه ویندوز");
                return;
            }
        }
        catch (Exception ex)
        {
            AppendLog("Windows entitlement check failed: " + ex.GetType().Name);
            MessageBox.Show(ex.Message, "بررسی سهمیه ویندوز ناموفق بود", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        ProfileStore.Instance.State.ActiveProfileId = chosen.Id;
        ProfileStore.Instance.Save();

        _connectScanCts = new CancellationTokenSource();
        _connectionBusy = true;
        var token = _connectScanCts.Token;
        ConnectButton.IsEnabled = true;
        HeaderStatus.Text = "در حال اتصال";
        ConnectionStatus.Text = $"در حال اتصال به {chosen.Name}…";
        ProbeStatus.Text = "در حال راه‌اندازی هسته و مسیر شبکه…";
        ProbeProgress.IsIndeterminate = true;
        ProbeProgress.Visibility = Visibility.Visible;
        StartOrbAnimation();

        try
        {
            AppendLog($"Starting selected profile only: {chosen.Name} ({chosen.Protocol})");
            await _connection.StartAsync(chosen, ProfileStore.Instance.State.Settings, token);
            if (!_connection.IsRunning)
                throw new InvalidOperationException("هسته اتصال بلافاصله پس از راه‌اندازی متوقف شد.");
            _connectedProfile = chosen;
            _connectedSince = DateTimeOffset.Now;
            _usageSessionId = Guid.NewGuid();
            _usageSequence = 0;
            _usageReportedDownloadBytes = 0;
            _usageReportedUploadBytes = 0;
            _usageTimer.Start();
            _isConnected = true;
            _sessionTimer.Start();
            UpdateSessionClock();
            ConnectionStatus.Text = ProfileStore.Instance.State.Settings.ConnectionMode == "TUN" ? "VPN فعال" : "Proxy فعال";
            HeaderStatus.Text = "متصل";
            StatusDot.Fill = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(80, 224, 181));
            ProbeStatus.Text = $"اتصال برقرار شد · {chosen.Name}";
            AppendLog($"Connected selected profile: {chosen.Name}");
        }
        catch (OperationCanceledException)
        {
            _connectedProfile = null;
            _isConnected = false;
            SetDisconnected("اتصال لغو شد");
            AppendLog("Connection cancelled by user.");
        }
        catch (Exception ex)
        {
            _connectedProfile = null;
            _isConnected = false;
            SetDisconnected("اتصال برقرار نشد");
            AppendLog("Connect flow failed: " + ex);
            MessageBox.Show(ex.Message + Environment.NewLine + Environment.NewLine + "برای جزئیات بیشتر، بخش گزارش اتصال را باز کن.",
                "خطا در اتصال", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _connectionBusy = false;
            _connectScanCts?.Dispose();
            _connectScanCts = null;
            ProbeProgress.IsIndeterminate = false;
            ProbeProgress.Visibility = Visibility.Collapsed;
            StopOrbAnimation();
            ConnectButton.IsEnabled = true;
            UpdateDashboard();
            if (_pendingEngineExit && !_closeAfterBusyOperation && !_closeAfterDisconnect)
            {
                _pendingEngineExit = false;
                if (_isConnected)
                    Dispatcher.BeginInvoke(new Action(() => _ = HandleUnexpectedEngineExitAsync()));
            }
            if (_closeAfterBusyOperation)
            {
                _closeAfterBusyOperation = false;
                if (_connection.IsRunning)
                {
                    await ReportWindowsUsageAsync(silent: true);
                    _usageTimer.Stop();
                    await _connection.StopAsync();
                }
                Close();
            }
        }
    }

    private async void SmartConnect_Click(object sender, RoutedEventArgs e)
    {
        if (_mandatoryWindowsUpdate)
        {
            MessageBox.Show("برای استفاده از Smart Connect ابتدا نسخه جدید برنامه را نصب کن.",
                "به‌روزرسانی اجباری", MessageBoxButton.OK, MessageBoxImage.Warning);
            Navigate(3, "تنظیمات شبکه", "به‌روزرسانی برنامه");
            return;
        }
        if (_connection.IsRunning || _connectionBusy || _manualScanBusy ||
            _cloudSyncRunning || _subscriptionRefreshRunning)
            return;

        try
        {
            await _license.EnsureFreeRegistrationAsync();
            var entitlement = await _license.GetStatusAsync(forceRefresh: true);
            UpdateWindowsLicenseUi(entitlement);
            if (!entitlement.Active)
            {
                MessageBox.Show("سهمیه رایگان ۷ گیبی‌بایتی این ماه و حجم خریداری‌شده ویندوز تمام شده است.",
                    "سهمیه اتصال تمام شده", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
        }
        catch (Exception error)
        {
            MessageBox.Show(error.Message, "بررسی سهمیه ویندوز", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SmartConnectButton.IsEnabled = false;
        ConnectButton.IsEnabled = false;
        ProbeProgress.IsIndeterminate = false;
        ProbeProgress.Value = 0;
        ProbeProgress.Visibility = Visibility.Visible;
        ProbeStatus.Text = "در حال دریافت فهرست سرورهای اختصاصی Moghimi…";

        try
        {
            await SyncCloudAsync(silent: true);
            var candidates = Profiles
                .Where(p => p.SubscriptionId == "moghimi-cloud")
                .ToList();

            if (candidates.Count == 0)
                throw new InvalidOperationException("هیچ سرور فعال Moghimi برای تست پیدا نشد.");

            _manualScanBusy = true;
            _manualScanCts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            var token = _manualScanCts.Token;
            using var throttle = new SemaphoreSlim(4, 4);
            var results = new System.Collections.Concurrent.ConcurrentBag<(ProxyProfile Profile, int Latency)>();
            var completed = 0;
            ProbeStatus.Text = $"در حال سنجش {candidates.Count} سرور با تست واقعی تونل…";
            StartOrbAnimation();

            var tasks = candidates.Select(async profile =>
            {
                await throttle.WaitAsync(token);
                try
                {
                    var latency = await ConnectionLatencyProbe.TestAsync(profile, token);
                    profile.LatencyMs = latency;
                    if (latency > 0) results.Add((profile, latency));
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception error)
                {
                    Dispatcher.BeginInvoke(() => AppendLog($"Smart Connect probe failed for {profile.Name}: {error.Message}"));
                    profile.LatencyMs = -1;
                }
                finally
                {
                    throttle.Release();
                    var done = Interlocked.Increment(ref completed);
                    Dispatcher.BeginInvoke(() =>
                    {
                        ProbeProgress.Value = 100d * done / candidates.Count;
                        ProbeStatus.Text = $"تست سرورها: {done} از {candidates.Count}";
                    });
                }
            }).ToArray();

            await Task.WhenAll(tasks);
            token.ThrowIfCancellationRequested();

            var best = results.OrderBy(x => x.Latency).FirstOrDefault();
            if (best.Profile is null)
                throw new InvalidOperationException("هیچ‌کدام از سرورهای Moghimi پاسخ معتبر ندادند.");

            ProfilesList.SelectedItem = best.Profile;
            _selectedProfile = best.Profile;
            SaveState();
            ProbeStatus.Text = $"بهترین سرور: {best.Profile.Name} · {best.Latency} ms";
            AppendLog($"Smart Connect selected {best.Profile.Name} at {best.Latency} ms.");

            _manualScanBusy = false;
            _manualScanCts.Dispose();
            _manualScanCts = null;
            ProbeProgress.Visibility = Visibility.Collapsed;
            StopOrbAnimation();
            SmartConnectButton.IsEnabled = true;
            ConnectButton.IsEnabled = true;

            ConnectButton_Click(ConnectButton, new RoutedEventArgs());
        }
        catch (OperationCanceledException)
        {
            ProbeStatus.Text = "تست هوشمند لغو شد یا زمان آن تمام شد.";
            AppendLog("Smart Connect scan cancelled or timed out.");
        }
        catch (Exception error)
        {
            ProbeStatus.Text = "انتخاب هوشمند سرور انجام نشد.";
            AppendLog("Smart Connect failed: " + error.Message);
            MessageBox.Show(error.Message, "Smart Connect", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _manualScanBusy = false;
            _manualScanCts?.Dispose();
            _manualScanCts = null;
            ProbeProgress.Visibility = Visibility.Collapsed;
            ProbeProgress.IsIndeterminate = false;
            StopOrbAnimation();
            SmartConnectButton.IsEnabled = true;
            ConnectButton.IsEnabled = true;
        }
    }

    private void StartOrbAnimation()
    {
        if (!IsLoaded) return;
        if (ConnectButton.Template.FindName("OrbPulse", ConnectButton) is FrameworkElement halo)
        {
            halo.BeginAnimation(OpacityProperty, null);
            halo.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0.18, 0.58, TimeSpan.FromSeconds(1.25))
            {
                AutoReverse = true,
                RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever,
                EasingFunction = new System.Windows.Media.Animation.SineEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut }
            });
        }

        if (ConnectButton.Template.FindName("OrbOrbitRotation", ConnectButton) is System.Windows.Media.RotateTransform orbit)
        {
            orbit.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, null);
            orbit.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty,
                new System.Windows.Media.Animation.DoubleAnimation(0, 360, TimeSpan.FromSeconds(7))
                {
                    RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever,
                    EasingFunction = new System.Windows.Media.Animation.LinearEase()
                });
        }
    }

    private void StopOrbAnimation()
    {
        if (ConnectButton?.Template?.FindName("OrbPulse", ConnectButton) is FrameworkElement halo)
        {
            halo.BeginAnimation(OpacityProperty, null);
            halo.Opacity = 0.26;
        }
        if (ConnectButton?.Template?.FindName("OrbOrbitRotation", ConnectButton) is System.Windows.Media.RotateTransform orbit)
        {
            orbit.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, null);
            orbit.Angle = 0;
        }
    }

    private void ManualProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_connectionBusy || _manualScanBusy || _cloudSyncRunning || _subscriptionRefreshRunning)
        {
            MessageBox.Show("تا پایان عملیات جاری، فهرست سرورها را تغییر نده.",
                "عملیات در حال اجرا", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dialog = new ProfileEditWindow { Owner = this };
        if (dialog.ShowDialog() != true) return;
        Profiles.Add(dialog.EditedProfile);
        ProfilesList.SelectedItem = dialog.EditedProfile;
        SaveState();
        AppendLog($"Manual profile added: {dialog.EditedProfile.Name}");
    }

    private void EditProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_connectionBusy || _manualScanBusy || _cloudSyncRunning || _subscriptionRefreshRunning)
        {
            MessageBox.Show("تا پایان عملیات جاری، فهرست سرورها را تغییر نده.",
                "عملیات در حال اجرا", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (_selectedProfile is null)
        {
            MessageBox.Show("ابتدا یک کانفیگ انتخاب کن.", "ویرایش کانفیگ",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (_selectedProfile.SubscriptionId == "moghimi-cloud")
        {
            MessageBox.Show("سرورهای اختصاصی Moghimi فقط قابل انتخاب و اتصال هستند و امکان ویرایش آن‌ها وجود ندارد.",
                "سرور محافظت‌شده", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var index = Profiles.IndexOf(_selectedProfile);
        if (index < 0) return;
        var dialog = new ProfileEditWindow(_selectedProfile) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        Profiles[index] = dialog.EditedProfile;
        ProfilesList.SelectedItem = dialog.EditedProfile;
        _selectedProfile = dialog.EditedProfile;
        SaveState();
        AppendLog($"Profile edited: {_selectedProfile.Name}");
    }

    private async void ImportQr_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "خواندن QR کانفیگ",
            Filter = "تصویر QR (*.png;*.jpg;*.jpeg;*.bmp;*.gif)|*.png;*.jpg;*.jpeg;*.bmp;*.gif|All files (*.*)|*.*",
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            var payload = await Task.Run(() => QrCodeService.DecodeFromImage(dialog.FileName));
            AddProfiles(ProfileCodec.DecodeMany(payload), Path.GetFileName(dialog.FileName));
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "خواندن QR ناموفق بود",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ShareProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedProfile is null)
        {
            MessageBox.Show("ابتدا یک کانفیگ انتخاب کن.", "اشتراک‌گذاری",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_selectedProfile.SubscriptionId == "moghimi-cloud")
        {
            MessageBox.Show("کپی و اشتراک‌گذاری کانفیگ‌های اختصاصی Moghimi غیرفعال است.",
                "محدودیت امنیتی", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            var confirm = MessageBox.Show(
                "لینک این کانفیگ شامل اطلاعات اتصال مانند رمز یا کلید سرور است. آن را فقط برای فرد مورداعتماد بفرست.\n\nادامه می‌دهی؟",
                "هشدار حریم خصوصی", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;
            Clipboard.SetText(ProfileCodec.Encode(_selectedProfile));
            MessageBox.Show("لینک کانفیگ در کلیپ‌بورد کپی شد.", "اشتراک‌گذاری",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "ساخت لینک ناموفق بود",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ExportQr_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedProfile is null)
        {
            MessageBox.Show("ابتدا یک کانفیگ انتخاب کن.", "خروجی QR",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_selectedProfile.SubscriptionId == "moghimi-cloud")
        {
            MessageBox.Show("ساخت QR از کانفیگ‌های اختصاصی Moghimi مجاز نیست.",
                "محدودیت امنیتی", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            var confirm = MessageBox.Show(
                "تصویر QR شامل اطلاعات کامل اتصال این کانفیگ است. آن را عمومی منتشر نکن.\n\nساخت QR را ادامه می‌دهی؟",
                "هشدار حریم خصوصی", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;
            var payload = ProfileCodec.Encode(_selectedProfile);
            var bytes = QrCodeService.CreatePng(payload);
            var safeName = new string(_selectedProfile.Name
                .Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch)
                .ToArray());
            var dialog = new SaveFileDialog
            {
                Title = "ذخیره تصویر QR",
                FileName = $"{safeName}.png",
                Filter = "PNG image (*.png)|*.png",
                DefaultExt = ".png",
                AddExtension = true
            };
            if (dialog.ShowDialog(this) != true) return;
            File.WriteAllBytes(dialog.FileName, bytes);
            AppendLog($"QR exported: {_selectedProfile.Name}");
            MessageBox.Show("تصویر QR ذخیره شد.", "خروجی QR",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "ساخت QR ناموفق بود",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void AddLink_Click(object sender, RoutedEventArgs e)
    {
        var text = PromptDialog.Show(this, "افزودن کانفیگ", "لینک V2Ray یا چند لینک را وارد کن:");
        if (string.IsNullOrWhiteSpace(text)) return;
        AddProfiles(ProfileCodec.DecodeMany(text), "لینک");
    }

    private void ImportFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog {
            Title = "واردکردن کانفیگ",
            Filter = "V2Ray / Sing-box links (*.txt;*.conf;*.uri)|*.txt;*.conf;*.uri|All files (*.*)|*.*",
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true) return;
        try { AddProfiles(ProfileCodec.DecodeMany(File.ReadAllText(dialog.FileName)), Path.GetFileName(dialog.FileName)); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "خطا در واردکردن فایل", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void PasteClipboard_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var clipboard = Clipboard.GetText();
            if (string.IsNullOrWhiteSpace(clipboard))
            {
                MessageBox.Show("کلیپ‌بورد خالی است یا متن قابل خواندن ندارد.", "واردکردن کانفیگ", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            AddProfiles(ProfileCodec.DecodeMany(clipboard), "کلیپ‌بورد");
        }
        catch (Exception ex)
        {
            MessageBox.Show("خواندن کلیپ‌بورد ممکن نشد: " + ex.Message, "خطا", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void AddProfiles(List<ProxyProfile> items, string source)
    {
        if (_connectionBusy || _manualScanBusy || _cloudSyncRunning || _subscriptionRefreshRunning)
        {
            MessageBox.Show("تا پایان عملیات جاری، فهرست سرورها را تغییر نده.",
                "عملیات در حال اجرا", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (items.Count == 0)
        {
            MessageBox.Show("هیچ لینک معتبری شناسایی نشد. قالب لینک را بررسی کن.", "ورودی نامعتبر", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        foreach (var p in items) Profiles.Add(p);
        if (ProfilesList.SelectedItem is null) ProfilesList.SelectedItem = items[0];
        SaveState();
        AppendLog($"{items.Count} profile(s) imported from {source}");
        UpdateDashboard();
    }

    private void RemoveProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_connectionBusy || _manualScanBusy || _cloudSyncRunning || _subscriptionRefreshRunning)
        {
            MessageBox.Show("تا پایان عملیات جاری، فهرست سرورها را تغییر نده.",
                "عملیات در حال اجرا", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (_selectedProfile is null) return;
        if (_selectedProfile.SubscriptionId == "moghimi-cloud")
        {
            MessageBox.Show("سرورهای اختصاصی Moghimi از پنل مدیریت می‌شوند و از این بخش حذف نمی‌شوند.",
                "سرور محافظت‌شده", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (_connection.IsRunning && _connectedProfile?.Id == _selectedProfile.Id)
        {
            MessageBox.Show("این کانفیگ در حال حاضر برای اتصال فعال استفاده می‌شود. ابتدا VPN/Proxy را قطع کن.",
                "کانفیگ در حال استفاده", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (MessageBox.Show($"کانفیگ «{_selectedProfile.Name}» حذف شود؟", "تأیید حذف", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        Profiles.Remove(_selectedProfile);
        _selectedProfile = null;
        ProfilesList.SelectedItem = Profiles.FirstOrDefault();
        SaveState();
        UpdateDashboard();
    }

    private async void ScanLatency_Click(object sender, RoutedEventArgs e)
    {
        if (_connectionBusy || _manualScanBusy || _cloudSyncRunning || _subscriptionRefreshRunning || _connection.IsRunning) return;
        var profile = ProfilesList.SelectedItem as ProxyProfile ?? _selectedProfile;
        if (profile is null)
        {
            MessageBox.Show("ابتدا یک سرور را از فهرست انتخاب کن.", "تست تأخیر", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var button = sender as Button;
        if (button is not null) button.IsEnabled = false;
        _manualScanBusy = true;
        _manualScanCts = new CancellationTokenSource();
        ProbeProgress.IsIndeterminate = true;
        ProbeProgress.Visibility = Visibility.Visible;
        ProbeStatus.Text = $"در حال تست دستی {profile.Name}…";
        StartOrbAnimation();
        try
        {
            var latency = await ConnectionLatencyProbe.TestAsync(profile, _manualScanCts.Token);
            profile.LatencyMs = latency;
            SelectedProfileLatency.Text = latency >= 0 ? $"{latency} ms" : "تست ناموفق";
            ProbeStatus.Text = latency >= 0 ? $"تأخیر {profile.Name}: {latency} ms" : $"سرور {profile.Name} به مقصدهای تست پاسخ نداد";
            SaveState();
            UpdateDashboard();
        }
        catch (OperationCanceledException) { ProbeStatus.Text = "تست لغو شد"; }
        catch (Exception ex)
        {
            ProbeStatus.Text = "تست انجام نشد";
            AppendLog($"Manual latency test failed for {profile.Name}: {ex}");
            MessageBox.Show(ex.Message, "تست تأخیر ناموفق بود", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _manualScanBusy = false;
            _manualScanCts?.Dispose();
            _manualScanCts = null;
            ProbeProgress.IsIndeterminate = false;
            ProbeProgress.Visibility = Visibility.Collapsed;
            StopOrbAnimation();
            if (button is not null) button.IsEnabled = true;
            if (_closeAfterBusyOperation) { _closeAfterBusyOperation = false; Close(); }
        }
    }

    private void AddSubscription_Click(object sender, RoutedEventArgs e)
    {
        if (_connectionBusy || _manualScanBusy || _cloudSyncRunning || _subscriptionRefreshRunning)
        {
            MessageBox.Show("تا پایان عملیات جاری، اشتراک جدید اضافه نکن.",
                "عملیات در حال اجرا", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var url = PromptDialog.Show(this, "افزودن اشتراک", "آدرس HTTPS یا HTTP اشتراک را وارد کن:");
        if (string.IsNullOrWhiteSpace(url)) return;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var subscriptionUri) ||
            subscriptionUri.Scheme is not ("https" or "http"))
        {
            MessageBox.Show("آدرس اشتراک معتبر نیست؛ باید با https:// یا http:// شروع شود.",
                "آدرس نامعتبر", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (subscriptionUri.Scheme == "http")
        {
            var proceed = MessageBox.Show(
                "این اشتراک از HTTP رمزگذاری‌نشده دریافت می‌شود. ممکن است لینک و کانفیگ‌ها در مسیر شبکه دیده یا تغییر داده شوند.\n\nفقط در صورت اعتماد کامل به منبع ادامه بده.",
                "هشدار امنیتی HTTP", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (proceed != MessageBoxResult.Yes) return;
        }
        var name = PromptDialog.Show(this, "نام اشتراک", "یک نام برای این اشتراک وارد کن:") ?? "اشتراک جدید";
        var sub = new SubscriptionItem { Url = url.Trim(), Name = string.IsNullOrWhiteSpace(name) ? "اشتراک جدید" : name.Trim() };
        Subscriptions.Add(sub);
        SubscriptionsList.SelectedItem = sub;
        SaveState();
    }

    private void RemoveSubscription_Click(object sender, RoutedEventArgs e)
    {
        if (_connectionBusy || _manualScanBusy || _cloudSyncRunning || _subscriptionRefreshRunning)
        {
            MessageBox.Show("تا پایان عملیات جاری، اشتراک را حذف نکن.",
                "عملیات در حال اجرا", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_selectedSubscription is null)
        {
            MessageBox.Show("ابتدا یک اشتراک انتخاب کن.", "حذف اشتراک",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_selectedSubscription.Id == "moghimi-cloud")
        {
            MessageBox.Show("گروه ابری مقیمی از داخل برنامه قابل حذف نیست؛ کانفیگ‌های آن با همگام‌سازی بعدی به‌روز می‌شوند.",
                "اشتراک سایت مقیمی", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var subscription = _selectedSubscription;
        var linked = Profiles.Where(p => p.SubscriptionId == subscription.Id).ToList();
        if (_connection.IsRunning && linked.Any(p => p.Id == _connectedProfile?.Id))
        {
            MessageBox.Show("یکی از کانفیگ‌های این اشتراک در اتصال فعال استفاده می‌شود. ابتدا اتصال را قطع کن.",
                "اشتراک در حال استفاده", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var confirm = MessageBox.Show(
            $"اشتراک «{subscription.Name}» و {linked.Count} کانفیگ مربوط به آن حذف شوند؟",
            "تأیید حذف اشتراک", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        foreach (var profile in linked) Profiles.Remove(profile);
        Subscriptions.Remove(subscription);
        _selectedSubscription = null;
        SubscriptionsList.SelectedItem = null;
        if (_selectedProfile is not null && !Profiles.Contains(_selectedProfile))
            _selectedProfile = Profiles.FirstOrDefault();
        ProfilesList.SelectedItem = _selectedProfile;
        SaveState();
        UpdateDashboard();
        AppendLog($"Subscription removed: {subscription.Name}");
    }

    private async void RefreshSubscription_Click(object sender, RoutedEventArgs e)
    {
        if (_connectionBusy || _manualScanBusy || _cloudSyncRunning || _subscriptionRefreshRunning || _connection.IsRunning)
        {
            MessageBox.Show("برای جلوگیری از تداخل، هنگام تست سرورها یا اتصال فعال، اشتراک‌ها به‌روزرسانی نمی‌شوند.",
                "عملیات در حال اجرا", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var subscription = _selectedSubscription;
        if (subscription is null)
        {
            MessageBox.Show("ابتدا یک اشتراک انتخاب کن.", "اشتراک", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _subscriptionRefreshRunning = true;
        subscription.LastStatus = "در حال دریافت و بررسی کانفیگ‌ها…";
        try
        {
            SaveState();
            // Keep the existing group intact until the new response is fully downloaded and parsed.
            var profiles = await _subscriptions.FetchAsync(subscription);
            var selectedFromThisSubscription = _selectedProfile?.SubscriptionId == subscription.Id;

            foreach (var old in Profiles.Where(p => p.SubscriptionId == subscription.Id).ToList())
                Profiles.Remove(old);
            foreach (var profile in profiles)
                Profiles.Add(profile);

            subscription.ProfileCount = profiles.Count;
            subscription.LastUpdated = DateTime.Now;
            subscription.LastStatus = $"{profiles.Count} کانفیگ دریافت شد";

            if (selectedFromThisSubscription)
                ProfilesList.SelectedItem = profiles.FirstOrDefault();

            SaveState();
            UpdateDashboard();
            AppendLog($"Subscription updated: {subscription.Name} ({profiles.Count} profiles)");
        }
        catch (OperationCanceledException)
        {
            subscription.LastStatus = "به‌روزرسانی لغو شد؛ کانفیگ‌های قبلی حفظ شدند.";
            try { SaveState(); } catch (Exception saveError) { AppendLog("Subscription status save failed: " + saveError.Message); }
        }
        catch (Exception ex)
        {
            subscription.LastStatus = "به‌روزرسانی ناموفق: " + ex.Message;
            try { SaveState(); } catch (Exception saveError) { AppendLog("Subscription status save failed: " + saveError.Message); }
            MessageBox.Show(ex.Message, "خطا در اشتراک", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _subscriptionRefreshRunning = false;
            // Do not steal focus if the user selected another subscription while this request was running.
        }
    }

    private async void CloudSync_Click(object sender, RoutedEventArgs e) =>
        await SyncCloudAsync(silent: false);

    private async Task SyncCloudAsync(bool silent)
    {
        if (_cloudSyncRunning || _subscriptionRefreshRunning) return;
        try
        {
            await _license.EnsureFreeRegistrationAsync();
        }
        catch (Exception error)
        {
            if (LicenseStatusText is not null) LicenseStatusText.Text = "آماده‌سازی سهمیه رایگان ویندوز ناموفق بود.";
            if (!silent) MessageBox.Show(error.Message, "ثبت سهمیه ویندوز", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        try
        {
            var license = await _license.GetStatusAsync();
            UpdateWindowsLicenseUi(license);
            if (!license.Active)
            {
                if (!silent) MessageBox.Show("اشتراک ویندوز فعال نیست یا اعتبار آن پایان یافته است.", "اشتراک غیرفعال", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
        }
        catch (Exception ex)
        {
            AppendLog("Windows license check failed: " + ex.Message);
            if (!silent) MessageBox.Show(ex.Message, "بررسی اشتراک ناموفق", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (_connectionBusy || _manualScanBusy)
        {
            if (!silent)
                MessageBox.Show("در حال تست سرورها هستیم؛ پس از پایان سنجش، همگام‌سازی را دوباره اجرا کن.",
                    "همگام‌سازی", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _cloudSyncRunning = true;
        HeaderStatus.Text = "همگام‌سازی سایت";
        try
        {
            // Fetch and parse everything first. Do not remove the previous working cloud group
            // unless the full active catalogue has been received successfully.
            var imported = await _cloudSync.FetchActiveProfilesAsync();
            var selectedWasCloud = _selectedProfile?.SubscriptionId == "moghimi-cloud";
            foreach (var old in Profiles.Where(p => p.SubscriptionId == "moghimi-cloud").ToList())
                Profiles.Remove(old);
            foreach (var profile in imported)
                Profiles.Add(profile);

            var cloud = Subscriptions.FirstOrDefault(s => s.Id == "moghimi-cloud");
            if (cloud is null)
            {
                cloud = new SubscriptionItem
                {
                    Id = "moghimi-cloud",
                    Name = "کانفیگ‌های سایت مقیمی",
                    Url = "Supabase / active entries"
                };
                Subscriptions.Add(cloud);
            }
            cloud.ProfileCount = imported.Count;
            cloud.LastUpdated = DateTime.Now;
            cloud.LastStatus = $"{imported.Count} کانفیگ فعال همگام شد";

            if (selectedWasCloud || _selectedProfile is null || !Profiles.Contains(_selectedProfile))
                ProfilesList.SelectedItem = imported.FirstOrDefault();
            SaveState();
            AppendLog($"Moghimi cloud sync: {imported.Count} profile(s).");
            if (!silent)
                MessageBox.Show($"{imported.Count} کانفیگ فعال از سایت دریافت شد.", "همگام‌سازی موفق", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            AppendLog("Moghimi cloud sync failed: " + ex.Message);
            if (!silent)
                MessageBox.Show(ex.Message, "همگام‌سازی ناموفق", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _cloudSyncRunning = false;
            HeaderStatus.Text = _connection.IsRunning ? "متصل" : "آماده اتصال";
            UpdateDashboard();
        }
    }


    private static long CurrentWindowsVersionCode()
    {
        var assembly = typeof(MainWindow).Assembly;
        // The release workflow sets Version=1.0.1-beta.1. AssemblyVersion remains
        // a stable CLR binding version, so update checks must use the informational SemVer.
        var informational = assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion;

        var parsed = ParseWindowsVersionCode(informational);
        if (parsed > 0) return parsed;

        var version = assembly.GetName().Version ?? new Version(1, 0, 0);
        return ((long)Math.Max(0, version.Major) * 1_000_000L) +
               ((long)Math.Max(0, version.Minor) * 1_000L) +
               Math.Max(0, version.Build);
    }

    private static long ParseWindowsVersionCode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return 0;
        var match = System.Text.RegularExpressions.Regex.Match(
            value, @"^(?<major>\d+)\.(?<minor>\d+)\.(?<patch>\d+)");
        if (!match.Success) return 0;
        if (!long.TryParse(match.Groups["major"].Value, out var major) ||
            !long.TryParse(match.Groups["minor"].Value, out var minor) ||
            !long.TryParse(match.Groups["patch"].Value, out var patch))
            return 0;
        if (major > 100_000 || minor > 999 || patch > 999) return 0;
        return major * 1_000_000L + minor * 1_000L + patch;
    }

    private async void CheckWindowsUpdate_Click(object sender, RoutedEventArgs e) =>
        await CheckWindowsUpdateAsync(silent: false);

    private async Task CheckWindowsUpdateAsync(bool silent)
    {
        if (_windowsUpdateCheckRunning) return;
        _windowsUpdateCheckRunning = true;
        CheckWindowsUpdateButton.IsEnabled = false;
        WindowsUpdateStatusText.Text = "در حال بررسی نسخه ویندوز…";
        try
        {
            var release = await _license.GetLatestReleaseAsync();
            var currentCode = CurrentWindowsVersionCode();
            if (release is null)
            {
                _availableWindowsRelease = null;
                _mandatoryWindowsUpdate = false;
                InstallWindowsUpdateButton.IsEnabled = false;
                WindowsUpdateStatusText.Text = "هنوز نسخه جدیدی برای ویندوز منتشر نشده است.";
                return;
            }

            var minimumCode = ParseWindowsVersionCode(release.MinimumSupportedVersion);
            var updateAvailable = release.VersionCode > currentCode;
            _mandatoryWindowsUpdate = updateAvailable &&
                (release.IsMandatory || (minimumCode > 0 && currentCode < minimumCode));

            if (!updateAvailable)
            {
                _availableWindowsRelease = null;
                _mandatoryWindowsUpdate = false;
                InstallWindowsUpdateButton.IsEnabled = false;
                WindowsUpdateStatusText.Text = $"برنامه به‌روز است · نسخه فعلی {typeof(MainWindow).Assembly.GetName().Version}";
                return;
            }

            _availableWindowsRelease = release;
            InstallWindowsUpdateButton.IsEnabled = true;
            var sizeMb = release.FileSize / 1048576d;
            var statusPrefix = _mandatoryWindowsUpdate ? "به‌روزرسانی اجباری" : "نسخه جدید آماده دریافت";
            WindowsUpdateStatusText.Text = $"{statusPrefix}: نسخه {release.VersionName} · {sizeMb:0} مگابایت";
            if (!string.IsNullOrWhiteSpace(release.ReleaseNotes))
                WindowsUpdateStatusText.Text += Environment.NewLine + release.ReleaseNotes;

            if (_mandatoryWindowsUpdate && !silent)
            {
                var answer = MessageBox.Show(
                    $"نسخه {release.VersionName} برای ادامه کار الزامی است. اکنون دریافت و نصب شود؟",
                    "به‌روزرسانی اجباری", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (answer == MessageBoxResult.Yes) await InstallWindowsUpdateAsync();
            }
        }
        catch (Exception error)
        {
            WindowsUpdateStatusText.Text = "بررسی نسخه جدید انجام نشد؛ اینترنت را بررسی کن.";
            AppendLog("Windows update check failed: " + error.GetType().Name);
            if (!silent)
                MessageBox.Show(error.Message, "بررسی به‌روزرسانی", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        finally
        {
            _windowsUpdateCheckRunning = false;
            CheckWindowsUpdateButton.IsEnabled = true;
        }
    }

    private async void InstallWindowsUpdate_Click(object sender, RoutedEventArgs e) =>
        await InstallWindowsUpdateAsync();

    private async Task InstallWindowsUpdateAsync()
    {
        var release = _availableWindowsRelease;
        if (release is null) return;

        InstallWindowsUpdateButton.IsEnabled = false;
        CheckWindowsUpdateButton.IsEnabled = false;
        try
        {
            WindowsUpdateStatusText.Text = $"در حال دانلود نسخه {release.VersionName}…";
            var progress = new Progress<double>(value =>
                WindowsUpdateStatusText.Text = $"دانلود نسخه {release.VersionName}: {value:P0}");

            var installer = await _license.DownloadAndVerifyInstallerAsync(release, progress);
            WindowsUpdateStatusText.Text = "SHA-256 تأیید شد؛ آماده اجرای نصب‌کننده.";

            var confirm = MessageBox.Show(
                "فایل نصب دریافت و سلامت آن با SHA-256 تأیید شد. برای نصب نسخه جدید، برنامه بسته می‌شود. ادامه می‌دهی؟",
                "آماده نصب به‌روزرسانی", MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (confirm != MessageBoxResult.Yes)
            {
                WindowsUpdateStatusText.Text = "نسخه دانلود و بررسی شد؛ نصب به درخواست کاربر متوقف شد.";
                return;
            }

            if (_connection.IsRunning)
            {
                await ReportWindowsUsageAsync(silent: true);
                _usageTimer.Stop();
                await _connection.StopAsync();
                _connectedProfile = null;
                _isConnected = false;
                SetDisconnected("آماده به‌روزرسانی");
            }

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = installer,
                UseShellExecute = true
            });
            Close();
        }
        catch (Exception error)
        {
            WindowsUpdateStatusText.Text = "دانلود یا نصب به‌روزرسانی انجام نشد.";
            AppendLog("Windows update install failed: " + error.GetType().Name);
            MessageBox.Show(error.Message, "به‌روزرسانی ویندوز ناموفق بود",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            InstallWindowsUpdateButton.IsEnabled = _availableWindowsRelease is not null;
            CheckWindowsUpdateButton.IsEnabled = true;
        }
    }

    private async void ActivateWindows_Click(object sender, RoutedEventArgs e)
    {
        var code = PromptDialog.Show(this, "فعال‌سازی اشتراک ویندوز", "کد فعال‌سازی دریافتی از بات تلگرام را وارد کن:");
        if (string.IsNullOrWhiteSpace(code)) return;

        ActivateWindowsButton.IsEnabled = false;
        LicenseStatusText.Text = "در حال بررسی و فعال‌سازی کد…";
        try
        {
            var status = await _license.ActivateAsync(code.Trim());
            UpdateWindowsLicenseUi(status);
            AppendLog("Windows activation completed.");
            await SyncCloudAsync(silent: true);
            MessageBox.Show("اشتراک ویندوز با موفقیت فعال شد.", "فعال‌سازی موفق", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            LicenseStatusText.Text = "فعال‌سازی انجام نشد";
            AppendLog("Windows activation failed: " + ex.Message);
            MessageBox.Show(ex.Message, "خطا در فعال‌سازی", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            ActivateWindowsButton.IsEnabled = true;
        }
    }

    private async void RefreshWindowsLicense_Click(object sender, RoutedEventArgs e) =>
        await RefreshWindowsLicenseStatusAsync(silent: false);

    private async Task RefreshWindowsLicenseStatusAsync(bool silent)
    {
        try
        {
            await _license.EnsureFreeRegistrationAsync();
            var status = await _license.GetStatusAsync(forceRefresh: true);
            UpdateWindowsLicenseUi(status);
        }
        catch (Exception ex)
        {
            LicenseStatusText.Text = "وضعیت اشتراک موقتاً در دسترس نیست؛ اینترنت را بررسی کن.";
            if (!silent) MessageBox.Show(ex.Message, "بررسی اشتراک", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task ReportWindowsUsageAsync(bool silent)
    {
        if (_usageSessionId == Guid.Empty)
            return;

        await _usageReportGate.WaitAsync();
        try
        {
            var snapshot = _connection.GetTrafficSnapshot();
            if (snapshot.DownloadBytes < _usageReportedDownloadBytes ||
                snapshot.UploadBytes < _usageReportedUploadBytes)
                throw new InvalidOperationException("شمارنده ترافیک هسته اتصال به‌صورت غیرمنتظره بازنشانی شد.");

            // Send cumulative upload/download checkpoints in bounded chunks.
            const long maxChunkBytes = 128L * 1024 * 1024;
            while (_usageReportedDownloadBytes < snapshot.DownloadBytes ||
                   _usageReportedUploadBytes < snapshot.UploadBytes)
            {
                var pendingDownload = snapshot.DownloadBytes - _usageReportedDownloadBytes;
                var pendingUpload = snapshot.UploadBytes - _usageReportedUploadBytes;
                var addDownload = Math.Min(pendingDownload, maxChunkBytes);
                var addUpload = Math.Min(pendingUpload, maxChunkBytes - addDownload);
                var nextDownload = _usageReportedDownloadBytes + addDownload;
                var nextUpload = _usageReportedUploadBytes + addUpload;
                var nextSequence = _usageSequence + 1;

                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                var report = await _license.ReportUsageAsync(
                    _usageSessionId, nextSequence, nextDownload, nextUpload, timeout.Token);

                // Checkpoints advance only after server acknowledgement. A lost response
                // can safely be retried with the same sequence because the RPC is idempotent.
                _usageSequence = nextSequence;
                _usageReportedDownloadBytes = nextDownload;
                _usageReportedUploadBytes = nextUpload;

                UpdateWindowsLicenseUi(new WindowsLicenseStatus
                {
                    Active = report.RemainingBytes > 0,
                    FreeLimitBytes = report.FreeLimitBytes,
                    FreeUsedBytes = report.FreeUsedBytes,
                    FreeRemainingBytes = report.FreeRemainingBytes,
                    PaidGrantedBytes = report.PaidGrantedBytes,
                    PaidUsedBytes = report.PaidUsedBytes,
                    PaidRemainingBytes = report.PaidRemainingBytes,
                    FreeMonthStart = report.FreeMonthStart,
                    GrantedBytes = report.PaidGrantedBytes,
                    UsedBytes = report.PaidUsedBytes,
                    RemainingBytes = report.PaidRemainingBytes,
                    ExpiresAt = report.ExpiresAt,
                    Status = report.RemainingBytes <= 0 ? "exhausted" : "active"
                });

                if (report.RemainingBytes <= 0)
                {
                    _usageTimer.Stop();
                    if (!_connectionBusy && _connection.IsRunning)
                        await ForceDisconnectAfterQuotaAsync();
                    return;
                }
            }

            // Also refresh while idle, so expiry or admin blocking is noticed without traffic.
            if (snapshot.DownloadBytes == _usageReportedDownloadBytes &&
                snapshot.UploadBytes == _usageReportedUploadBytes)
            {
                var entitlement = await _license.GetStatusAsync(forceRefresh: true);
                UpdateWindowsLicenseUi(entitlement);
                if (!entitlement.Active && !_connectionBusy && _connection.IsRunning)
                {
                    _usageTimer.Stop();
                    await ForceDisconnectAfterQuotaAsync();
                }
            }
        }
        catch (Exception error)
        {
            AppendLog("Windows usage report deferred: " + error.GetType().Name);
            if (!silent)
                MessageBox.Show("ثبت مصرف انجام نشد؛ برنامه ترافیک را نگه می‌دارد و در گزارش بعدی دوباره می‌فرستد.",
                    "گزارش مصرف", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        finally
        {
            _usageReportGate.Release();
        }
    }

    private async Task HandleUnexpectedEngineExitAsync()
    {
        if (_reconnectInProgress || _connectionBusy || _connection.IsRunning || !_isConnected ||
            _connectedProfile is null || _closeAfterDisconnect || _closeAfterBusyOperation)
            return;

        _reconnectInProgress = true;
        _connectionBusy = true;
        var profile = _connectedProfile;
        var settings = ProfileStore.Instance.State.Settings;
        _usageTimer.Stop();
        ConnectionStatus.Text = "ارتباط قطع شد · در حال بازیابی";
        HeaderStatus.Text = "تلاش برای اتصال مجدد";
        ProbeStatus.Text = "در حال بررسی شبکه و اشتراک…";
        ConnectButton.IsEnabled = false;

        var retryAgain = false;
        try
        {
            // Flush the final local counters before the disconnected state clears them.
            await ReportWindowsUsageAsync(silent: true);
            SetDisconnected("ارتباط ناپایدار شد");
            ProbeProgress.Visibility = Visibility.Visible;
            ProbeProgress.IsIndeterminate = true;
            StartOrbAnimation();

            // TUN startup requires an explicit Windows UAC approval; retry once to avoid repeated prompts.
            // System Proxy does not require elevation, so it can use the full bounded retry schedule.
            var retryDelays = settings.ConnectionMode == "TUN"
                ? new[] { 1000 }
                : new[] { 1000, 2000, 4000 };
            Exception? lastError = null;
            for (var attempt = 0; attempt < retryDelays.Length; attempt++)
            {
                await Task.Delay(retryDelays[attempt]);
                if (_closeAfterDisconnect || _closeAfterBusyOperation) break;

                if (!NetworkInterface.GetIsNetworkAvailable())
                {
                    lastError = new InvalidOperationException("شبکه در دسترس نیست.");
                    AppendLog($"Auto-reconnect attempt {attempt + 1}: no available network interface.");
                    continue;
                }

                try
                {
                    if (string.Equals(profile.SubscriptionId, "moghimi-cloud", StringComparison.Ordinal))
                    {
                        var license = await _license.GetStatusAsync(forceRefresh: true);
                        if (!license.Active)
                            throw new InvalidOperationException("اشتراک ویندوز فعال نیست یا حجم آن تمام شده است.");
                    }

                    await _connection.StartAsync(profile, settings);
                    if (!_connection.IsRunning)
                        throw new InvalidOperationException("هسته پس از تلاش مجدد متوقف شد.");

                    _connectedProfile = profile;
                    _selectedProfile = profile;
                    ProfilesList.SelectedItem = profile;
                    _connectedSince = DateTimeOffset.Now;
                    _isConnected = true;
                    if (string.Equals(profile.SubscriptionId, "moghimi-cloud", StringComparison.Ordinal))
                    {
                        _usageSessionId = Guid.NewGuid();
                        _usageSequence = 0;
                        _usageReportedDownloadBytes = 0;
                        _usageReportedUploadBytes = 0;
                        _usageTimer.Start();
                    }
                    _sessionTimer.Start();
                    UpdateSessionClock();

                    ConnectionStatus.Text = settings.ConnectionMode == "TUN"
                        ? "VPN فعال · اتصال بازیابی شد"
                        : "Proxy فعال · اتصال بازیابی شد";
                    HeaderStatus.Text = "متصل";
                    StatusDot.Fill = new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(80, 224, 181));
                    ProbeStatus.Text = $"اتصال بازیابی شد · {profile.Name}";
                    AppendLog($"Auto-reconnect succeeded on attempt {attempt + 1}.");
                    SaveState();
                    return;
                }
                catch (Exception error)
                {
                    lastError = error;
                    AppendLog($"Auto-reconnect attempt {attempt + 1} failed: {error.GetType().Name}");
                    if (_connection.IsRunning)
                    {
                        try { await _connection.StopAsync(); } catch { }
                    }
                }
            }

            SetDisconnected("اتصال بازیابی نشد؛ دوباره تلاش کن");
            ProbeStatus.Text = lastError is null
                ? "اتصال اینترنت را بررسی کن و دوباره وصل شو."
                : "تلاش مجدد ناموفق بود؛ سرور یا اینترنت را بررسی کن.";
            AppendLog("Auto-reconnect exhausted its bounded retry attempts.");
        }
        catch (Exception error)
        {
            SetDisconnected("اتصال بازیابی نشد");
            AppendLog("Auto-reconnect handler failed: " + error.GetType().Name);
        }
        finally
        {
            _connectionBusy = false;
            _reconnectInProgress = false;
            retryAgain = _pendingEngineExit && _isConnected && !_connection.IsRunning && !_closeAfterDisconnect;
            _pendingEngineExit = false;
            ProbeProgress.IsIndeterminate = false;
            ProbeProgress.Visibility = Visibility.Collapsed;
            StopOrbAnimation();
            ConnectButton.IsEnabled = true;

            if (_closeAfterBusyOperation)
            {
                _closeAfterBusyOperation = false;
                if (_connection.IsRunning)
                {
                    await ReportWindowsUsageAsync(silent: true);
                    _usageTimer.Stop();
                    await _connection.StopAsync();
                }
                Close();
            }
            else
            {
                UpdateDashboard();
                if (retryAgain)
                    Dispatcher.BeginInvoke(new Action(() => _ = HandleUnexpectedEngineExitAsync()));
            }
        }
    }

    private async Task ForceDisconnectAfterQuotaAsync()
    {
        if (!_connection.IsRunning || _connectionBusy) return;
        _connectionBusy = true;
        ConnectButton.IsEnabled = false;
        try
        {
            await _connection.StopAsync();
            _connectedProfile = null;
            _isConnected = false;
            _usageSessionId = Guid.Empty;
            SetDisconnected("حجم اشتراک تمام شد");
            UpdateDashboard();
            MessageBox.Show("حجم اشتراک ویندوز به پایان رسید؛ اتصال قطع شد.",
                "اتمام حجم اشتراک", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception error)
        {
            AppendLog("Quota disconnect failed: " + error.GetType().Name);
        }
        finally
        {
            _connectionBusy = false;
            ConnectButton.IsEnabled = true;
            UpdateDashboard();
        }
    }

    private void UpdateWindowsLicenseUi(WindowsLicenseStatus status)
    {
        if (LicenseStatusText is null) return;

        var freeLimitGb = status.FreeLimitBytes / 1073741824d;
        var freeUsedGb = status.FreeUsedBytes / 1073741824d;
        var freeRemainingGb = status.FreeRemainingBytes / 1073741824d;
        if (FreeQuotaSummaryText is not null)
            FreeQuotaSummaryText.Text = $"{freeRemainingGb:0.##} گیبی‌بایت باقی‌مانده از {freeLimitGb:0} گیبی‌بایت";
        if (FreeQuotaDetailText is not null)
        {
            var monthLabel = DateOnly.TryParse(status.FreeMonthStart, out var monthStart)
                ? monthStart.ToString("yyyy/MM")
                : "ماه جاری UTC";
            FreeQuotaDetailText.Text = $"{freeUsedGb:0.##} GB مصرف‌شده · بازنشانی: {monthLabel}";
        }
        if (FreeUsageProgress is not null && status.FreeLimitBytes > 0)
            FreeUsageProgress.Value = Math.Clamp(100d * status.FreeUsedBytes / status.FreeLimitBytes, 0d, 100d);

        var paidRemainingGb = status.PaidRemainingBytes / 1073741824d;
        if (PaidQuotaSummaryText is not null)
            PaidQuotaSummaryText.Text = status.PaidRemainingBytes > 0
                ? $"{paidRemainingGb:0.##} GB حجم خریداری‌شده باقی‌مانده"
                : "حجم خریداری‌شده فعال نیست";
        if (SubscriptionSummaryText is not null)
            SubscriptionSummaryText.Text = status.Active
                ? "دسترسی ویندوز فعال است"
                : "سهمیه قابل استفاده تمام شده است";
        if (SubscriptionUsageProgress is not null && status.PaidGrantedBytes > 0)
            SubscriptionUsageProgress.Value = Math.Clamp(100d * status.PaidUsedBytes / status.PaidGrantedBytes, 0d, 100d);

        var freeMonth = status.FreeMonthStart ?? "ماه جاری UTC";
        LicenseStatusText.Text = status.Active
            ? $"ویندوز فعال · {freeRemainingGb:0.##} GB سهمیه رایگان · {paidRemainingGb:0.##} GB حجم خریداری‌شده · دوره {freeMonth}"
            : status.Status == "exhausted"
                ? "سهمیه رایگان و حجم خریداری‌شده تمام شده است."
                : "سهمیه ویندوز در دسترس نیست؛ وضعیت اتصال را بررسی کن.";
    }

    private void RefreshLogs_Click(object sender, RoutedEventArgs e) => RefreshLogs();
    private void RefreshLogs()
    {
        LogBox.Text = ConnectionManager.ReadLogTail() + Environment.NewLine + Environment.NewLine + "---- Application ----" + Environment.NewLine + string.Join(Environment.NewLine, _appLog.TakeLast(70));
        LogBox.ScrollToEnd();
    }

    private readonly List<string> _appLog = [];
    private void AppendLog(string line)
    {
        _appLog.Add($"{DateTime.Now:HH:mm:ss}  {line}");
        RefreshLogs();
    }

    private void SaveState()
    {
        ProfileStore.Instance.State.Profiles = Profiles.Where(p => p.SubscriptionId != "moghimi-cloud").ToList();
        ProfileStore.Instance.State.Subscriptions = Subscriptions.ToList();
        ProfileStore.Instance.State.ActiveProfileId = _selectedProfile?.Id ?? (ProfilesList.SelectedItem as ProxyProfile)?.Id;
        ProfileStore.Instance.Save();
        UpdateDashboard();
    }

    private void OnPropertyChanged(string property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));

    private bool _closeAfterDisconnect;
    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if ((_connectionBusy || _manualScanBusy) && !_closeAfterBusyOperation)
        {
            e.Cancel = true;
            _closeAfterBusyOperation = true;
            _connectScanCts?.Cancel();
            _manualScanCts?.Cancel();
            return;
        }

        if (!_closeAfterDisconnect && _connection.IsRunning)
        {
            e.Cancel = true;
            _closeAfterDisconnect = true;
            _logTimer.Stop();
            _cloudTimer.Stop();
            _sessionTimer.Stop();
            _usageTimer.Stop();
            IsEnabled = false;
            try
            {
                await ReportWindowsUsageAsync(silent: true);
                await _connection.StopAsync();
            }
            finally
            {
                _connection.Dispose();
                SystemProxyController.Restore();
                Close();
            }
            return;
        }

        _logTimer.Stop();
        _cloudTimer.Stop();
        _sessionTimer.Stop();
        _usageTimer.Stop();
        _connection.Dispose();
        SystemProxyController.Restore();
    }
}

internal static class PromptDialog
{
    public static string? Show(Window owner, string title, string message)
    {
        var window = new Window {
            Title = title, Owner = owner, Width = 560, Height = 260,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize, Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(20,24,33)),
            Foreground = System.Windows.Media.Brushes.White, FlowDirection = FlowDirection.RightToLeft
        };
        var panel = new StackPanel { Margin = new Thickness(22) };
        panel.Children.Add(new TextBlock { Text = message, FontSize = 14, Margin = new Thickness(0,0,0,14) });
        var input = new TextBox { MinHeight = 42, VerticalContentAlignment = VerticalAlignment.Center };
        panel.Children.Add(input);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0,18,0,0) };
        var ok = new Button { Content = "تأیید", Width = 100, Margin = new Thickness(5,0,5,0), IsDefault = true };
        var cancel = new Button { Content = "انصراف", Width = 100, Margin = new Thickness(5,0,5,0), IsCancel = true };
        ok.Click += (_, _) => window.DialogResult = true;
        buttons.Children.Add(ok); buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
        window.Content = panel;
        input.Focus();
        return window.ShowDialog() == true ? input.Text : null;
    }
}
