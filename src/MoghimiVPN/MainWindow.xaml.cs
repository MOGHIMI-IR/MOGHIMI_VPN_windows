using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace MoghimiVPN;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly ConnectionManager _connection = new();
    private readonly CloudSync _cloudSync = new();
    private readonly SubscriptionService _subscriptions = new();
    private readonly DispatcherTimer _logTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _cloudTimer = new() { Interval = TimeSpan.FromHours(6) };
    private bool _cloudSyncRunning;
    private ProxyProfile? _selectedProfile;
    private SubscriptionItem? _selectedSubscription;
    private bool _isConnected;

    public ObservableCollection<ProxyProfile> Profiles { get; } = [];
    public ObservableCollection<SubscriptionItem> Subscriptions { get; } = [];
    public event PropertyChangedEventHandler? PropertyChanged;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        foreach (var p in ProfileStore.Instance.State.Profiles) Profiles.Add(p);
        foreach (var s in ProfileStore.Instance.State.Subscriptions) Subscriptions.Add(s);
        var active = Profiles.FirstOrDefault(p => p.Id == ProfileStore.Instance.State.ActiveProfileId) ?? Profiles.FirstOrDefault();
        if (active is not null) ProfilesList.SelectedItem = active;
        LoadSettings();
        UpdateDashboard();
        _connection.EngineExited += (_, _) => Dispatcher.Invoke(() => SetDisconnected("اتصال توسط هسته متوقف شد"));
        _connection.LogMessage += (_, message) => Dispatcher.Invoke(() => AppendLog(message));
        _logTimer.Tick += (_, _) => RefreshLogs();
        _logTimer.Start();
        _cloudTimer.Tick += async (_, _) => await SyncCloudAsync(silent: true);
        _cloudTimer.Start();
        Loaded += async (_, _) => await SyncCloudAsync(silent: true);
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
        s.ConnectionMode = (ConnectionModeCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "TUN";
        s.DnsServer = string.IsNullOrWhiteSpace(DnsTextBox.Text) ? "1.1.1.1" : DnsTextBox.Text.Trim();
        s.BlockAds = BlockAdsCheck.IsChecked == true;
        s.BypassLan = BypassLanCheck.IsChecked == true;
        ProfileStore.Instance.Save();
        UpdateDashboard();
    }

    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        SaveSettingsFromUI();
        MessageBox.Show("تنظیمات ذخیره شد.", "Moghimi VPN", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ProfilesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selectedProfile = ProfilesList.SelectedItem as ProxyProfile;
        if (_selectedProfile is not null)
            ProfileStore.Instance.State.ActiveProfileId = _selectedProfile.Id;
        UpdateDashboard();
        ProfileStore.Instance.Save();
    }

    private void SubscriptionsList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        _selectedSubscription = SubscriptionsList.SelectedItem as SubscriptionItem;

    private void UpdateDashboard()
    {
        var selected = _selectedProfile ?? Profiles.FirstOrDefault();
        SelectedProfileName.Text = selected?.Name ?? "هنوز سروری انتخاب نشده";
        SelectedProfileEndpoint.Text = selected is null ? "از بخش کانفیگ‌ها یک سرور اضافه کن" : $"{selected.Protocol.ToUpperInvariant()}  ·  {selected.Server}:{selected.Port}";
        SelectedProfileLatency.Text = selected is null || selected.LatencyMs < 0 ? "پینگ آزمایش نشده" : $"{selected.LatencyMs} ms";
        ProfileCountSummary.Text = Profiles.Count.ToString();
        DnsSummary.Text = ProfileStore.Instance.State.Settings.DnsServer;
        ModeSummary.Text = ProfileStore.Instance.State.Settings.ConnectionMode == "TUN" ? "TUN سراسری" : "System Proxy";
        ConnectionStatus.Text = _isConnected ? "متصل" : "قطع";
        ConnectButton.Content = _isConnected ? "قطع اتصال" : "اتصال امن";
        HeaderStatus.Text = _isConnected ? "تونل فعال" : "آماده اتصال";
        StatusDot.Fill = _isConnected ? System.Windows.Media.Brushes.LimeGreen : System.Windows.Media.Brushes.Goldenrod;
        OnPropertyChanged(nameof(Profiles));
    }

    private void SetDisconnected(string message)
    {
        _isConnected = false;
        ConnectionStatus.Text = message;
        ConnectButton.Content = "اتصال امن";
        HeaderStatus.Text = "قطع";
        StatusDot.Fill = System.Windows.Media.Brushes.Goldenrod;
        RefreshLogs();
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_connection.IsRunning)
        {
            await _connection.StopAsync();
            _isConnected = false;
            UpdateDashboard();
            return;
        }

        var profile = ProfilesList.SelectedItem as ProxyProfile ?? _selectedProfile ?? Profiles.FirstOrDefault();
        if (profile is null)
        {
            MessageBox.Show("اول یک کانفیگ اضافه و انتخاب کن.", "Moghimi VPN", MessageBoxButton.OK, MessageBoxImage.Information);
            Navigate(1, "مدیریت کانفیگ‌ها", "سرورها را اضافه، انتخاب و تست کن");
            return;
        }

        SaveSettingsFromUI();
        ConnectButton.IsEnabled = false;
        HeaderStatus.Text = "در حال آماده‌سازی";
        ConnectionStatus.Text = "در حال بررسی سرور و تنظیمات…";
        try
        {
            await _connection.StartAsync(profile, ProfileStore.Instance.State.Settings);
            _isConnected = true;
            ConnectionStatus.Text = "تونل فعال";
            HeaderStatus.Text = "متصل";
            StatusDot.Fill = System.Windows.Media.Brushes.LimeGreen;
            AppendLog($"Connected profile: {profile.Name}");
        }
        catch (Exception ex)
        {
            _isConnected = false;
            SetDisconnected("اتصال برقرار نشد");
            MessageBox.Show(ex.Message, "خطا در اتصال", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            ConnectButton.IsEnabled = true;
            UpdateDashboard();
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
        if (_selectedProfile is null) return;
        if (MessageBox.Show($"کانفیگ «{_selectedProfile.Name}» حذف شود؟", "تأیید حذف", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        Profiles.Remove(_selectedProfile);
        _selectedProfile = null;
        ProfilesList.SelectedItem = Profiles.FirstOrDefault();
        SaveState();
        UpdateDashboard();
    }

    private async void ScanLatency_Click(object sender, RoutedEventArgs e)
    {
        if (Profiles.Count == 0) return;
        var button = sender as Button;
        if (button is not null) button.IsEnabled = false;
        try
        {
            var gate = new SemaphoreSlim(8);
            var tasks = Profiles.Select(async profile =>
            {
                await gate.WaitAsync();
                try
                {
                    var latency = await ConnectionManager.PingAsync(profile.Server, profile.Port);
                    await Dispatcher.InvokeAsync(() => profile.LatencyMs = latency);
                }
                finally { gate.Release(); }
            });
            await Task.WhenAll(tasks);
            var best = Profiles.Where(p => p.LatencyMs >= 0).OrderBy(p => p.LatencyMs).FirstOrDefault();
            if (best is not null && _selectedProfile is null) ProfilesList.SelectedItem = best;
            AppendLog("Latency scan completed.");
            UpdateDashboard();
        }
        finally { if (button is not null) button.IsEnabled = true; }
    }

    private void AddSubscription_Click(object sender, RoutedEventArgs e)
    {
        var url = PromptDialog.Show(this, "افزودن اشتراک", "آدرس HTTPS یا HTTP اشتراک را وارد کن:");
        if (string.IsNullOrWhiteSpace(url)) return;
        var name = PromptDialog.Show(this, "نام اشتراک", "یک نام برای این اشتراک وارد کن:") ?? "اشتراک جدید";
        var sub = new SubscriptionItem { Url = url.Trim(), Name = string.IsNullOrWhiteSpace(name) ? "اشتراک جدید" : name.Trim() };
        Subscriptions.Add(sub);
        SubscriptionsList.SelectedItem = sub;
        SaveState();
    }

    private async void RefreshSubscription_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedSubscription is null)
        {
            MessageBox.Show("ابتدا یک اشتراک انتخاب کن.", "اشتراک", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try
        {
            var profiles = await _subscriptions.FetchAsync(_selectedSubscription);
            foreach (var old in Profiles.Where(p => p.SubscriptionId == _selectedSubscription.Id).ToList()) Profiles.Remove(old);
            foreach (var profile in profiles) Profiles.Add(profile);
            _selectedSubscription.ProfileCount = profiles.Count;
            _selectedSubscription.LastUpdated = DateTime.Now;
            _selectedSubscription.LastStatus = $"{profiles.Count} کانفیگ دریافت شد";
            SaveState();
            UpdateDashboard();
            AppendLog($"Subscription updated: {_selectedSubscription.Name}");
        }
        catch (Exception ex)
        {
            _selectedSubscription.LastStatus = "به‌روزرسانی ناموفق: " + ex.Message;
            SaveState();
            MessageBox.Show(ex.Message, "خطا در اشتراک", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void CloudSync_Click(object sender, RoutedEventArgs e) =>
        await SyncCloudAsync(silent: false);

    private async Task SyncCloudAsync(bool silent)
    {
        if (_cloudSyncRunning) return;
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
        ProfileStore.Instance.State.Profiles = Profiles.ToList();
        ProfileStore.Instance.State.Subscriptions = Subscriptions.ToList();
        ProfileStore.Instance.State.ActiveProfileId = _selectedProfile?.Id ?? (ProfilesList.SelectedItem as ProxyProfile)?.Id;
        ProfileStore.Instance.Save();
        UpdateDashboard();
    }

    private void OnPropertyChanged(string property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));

    private bool _closeAfterDisconnect;
    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!_closeAfterDisconnect && _connection.IsRunning)
        {
            e.Cancel = true;
            _closeAfterDisconnect = true;
            _logTimer.Stop();
            IsEnabled = false;
            try { await _connection.StopAsync(); }
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
        var ok = new Button { Content = "تأیید", Width = 100, Margin = new Thickness(5,0), IsDefault = true };
        var cancel = new Button { Content = "انصراف", Width = 100, Margin = new Thickness(5,0), IsCancel = true };
        ok.Click += (_, _) => window.DialogResult = true;
        buttons.Children.Add(ok); buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
        window.Content = panel;
        input.Focus();
        return window.ShowDialog() == true ? input.Text : null;
    }
}
