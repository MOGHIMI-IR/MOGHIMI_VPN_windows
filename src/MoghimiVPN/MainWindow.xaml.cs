using Microsoft.Win32;
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
    private readonly CloudSync _cloudSync = new();
    private readonly SubscriptionService _subscriptions = new();
    private readonly DispatcherTimer _logTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _cloudTimer = new() { Interval = TimeSpan.FromHours(6) };
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

    private void SetDisconnected(string message)
    {
        _isConnected = false;
        _connectedProfile = null;
        ConnectionStatus.Text = message;
        ProbeStatus.Text = "آماده اتصال هوشمند";
        ProbeProgress.Visibility = Visibility.Collapsed;
        ConnectButton.Content = "اتصال امن";
        HeaderStatus.Text = "قطع";
        StopOrbAnimation();
        StatusDot.Fill = System.Windows.Media.Brushes.Goldenrod;
        RefreshLogs();
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_subscriptionRefreshRunning)
        {
            MessageBox.Show("به‌روزرسانی اشتراک هنوز تمام نشده؛ بعد از پایان آن اتصال را بزن.",
                "Moghimi VPN", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_manualScanBusy)
        {
            MessageBox.Show("تست دستی سرورها در حال اجراست؛ پس از پایان آن اتصال را شروع کن.",
                "Moghimi VPN", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_connectionBusy)
        {
            _connectScanCts?.Cancel();
            ProbeStatus.Text = "درخواست لغو ثبت شد…";
            return;
        }

        if (_cloudSyncRunning)
        {
            MessageBox.Show("همگام‌سازی کانفیگ‌های سایت هنوز تمام نشده؛ بعد از پایان دریافت دوباره اتصال را بزن.",
                "Moghimi VPN", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_connection.IsRunning)
        {
            await _connection.StopAsync();
            _connectedProfile = null;
            _isConnected = false;
            SetDisconnected("اتصال قطع شد");
            UpdateDashboard();
            return;
        }

        if (Profiles.Count == 0)
        {
            MessageBox.Show("اول یک کانفیگ اضافه کن یا کانفیگ‌های سایت مقیمی را همگام‌سازی کن.",
                "Moghimi VPN", MessageBoxButton.OK, MessageBoxImage.Information);
            Navigate(1, "مدیریت کانفیگ‌ها", "سرورها را اضافه، انتخاب و تست کن");
            return;
        }

        SaveSettingsFromUI();
        var selectedAtStart = (ProfilesList.SelectedItem as ProxyProfile)?.Id;
        _connectScanCts = new CancellationTokenSource();
        _connectionBusy = true;
        var token = _connectScanCts.Token;
        ConnectButton.IsEnabled = true;
        HeaderStatus.Text = "در حال بررسی";
        ConnectionStatus.Text = "در حال انتخاب بهترین سرور…";
        ProbeStatus.Text = "در حال سنجش سرورها · برای لغو دوباره دکمه اتصال را بزن";
        ProbeProgress.Visibility = Visibility.Visible;
        ProbeProgress.Value = 0;
        StartOrbAnimation();

        try
        {
            var best = await ProbeAllProfilesAsync(token);

            token.ThrowIfCancellationRequested();

            // A new manual selection made while probes were running always wins,
            // matching the Android client's guarded selection behavior.
            var currentSelection = ProfilesList.SelectedItem as ProxyProfile;
            var userChangedSelection = currentSelection is not null &&
                                       currentSelection.Id != selectedAtStart;
            var chosen = userChangedSelection ? currentSelection : best;

            if (chosen is null)
            {
                var unsupported = Profiles
                    .Where(p => !SingBoxConfigBuilder.IsTransportSupported(p.Network))
                    .Select(p => string.IsNullOrWhiteSpace(p.Network) ? "نامشخص" : p.Network)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (unsupported.Count > 0)
                    throw new InvalidOperationException(
                        "هیچ سرور قابل اتصالی پیدا نشد. بعضی پروفایل‌ها از انتقال پشتیبانی‌نشده استفاده می‌کنند: " +
                        string.Join("، ", unsupported) +
                        ". در این نسخه TCP، WS، gRPC، HTTP/2، HTTPUpgrade و QUIC پشتیبانی می‌شوند.");
                throw new InvalidOperationException(
                    "هیچ سرور پاسخ‌گویی پیدا نشد. اتصال شروع نشد؛ کانفیگ‌ها یا اینترنت را بررسی کن.");
            }

            ProfilesList.SelectedItem = chosen;
            _selectedProfile = chosen;
            ProfileStore.Instance.State.ActiveProfileId = chosen.Id;
            ProfileStore.Instance.Save();

            ProbeStatus.Text = chosen.LatencyMs >= 0
                ? $"بهترین مسیر: {chosen.Name}  ·  {chosen.LatencyMs} ms"
                : $"انتخاب دستی: {chosen.Name}";

            ConnectionStatus.Text = "سرور انتخاب شد؛ در حال برقراری اتصال…";
            HeaderStatus.Text = "در حال اتصال";
            AppendLog($"Connect flow selected: {chosen.Name} ({chosen.LatencyMs} ms)");

            await _connection.StartAsync(chosen, ProfileStore.Instance.State.Settings, token);

            _connectedProfile = chosen;
            _isConnected = true;
            ConnectionStatus.Text = ProfileStore.Instance.State.Settings.ConnectionMode == "TUN"
                ? "VPN فعال"
                : "Proxy فعال";
            HeaderStatus.Text = "متصل";
            StatusDot.Fill = System.Windows.Media.Brushes.LimeGreen;
            ProbeStatus.Text = $"اتصال برقرار شد · {chosen.Name}";
            AppendLog($"Connected profile: {chosen.Name}");
        }
        catch (OperationCanceledException)
        {
            _connectedProfile = null;
            _isConnected = false;
            SetDisconnected("تست یا اتصال لغو شد");
            AppendLog("Connect flow cancelled by user.");
        }
        catch (Exception ex)
        {
            _connectedProfile = null;
            _isConnected = false;
            SetDisconnected("اتصال برقرار نشد");
            AppendLog("Connect flow failed: " + ex.Message);
            MessageBox.Show(ex.Message, "خطا در اتصال", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _connectionBusy = false;
            _connectScanCts?.Dispose();
            _connectScanCts = null;
            ProbeProgress.Visibility = Visibility.Collapsed;
            StopOrbAnimation();
            ConnectButton.IsEnabled = true;
            UpdateDashboard();
            if (_closeAfterBusyOperation)
            {
                _closeAfterBusyOperation = false;
                if (_connection.IsRunning) await _connection.StopAsync();
                Close();
            }
        }
    }

    private async Task<ProxyProfile?> ProbeAllProfilesAsync(CancellationToken cancellationToken)
    {
        var candidates = Profiles.ToList();
        if (candidates.Count == 0) return null;

        var completed = 0;
        var gate = new SemaphoreSlim(4, 4);
        var tasks = candidates.Select(async profile =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                var latency = await ConnectionLatencyProbe.TestAsync(profile, cancellationToken);
                await Dispatcher.InvokeAsync(() =>
                {
                    profile.LatencyMs = latency;
                    completed++;
                    ProbeProgress.Value = completed * 100.0 / candidates.Count;
                    ProbeStatus.Text = $"در حال بررسی {completed} از {candidates.Count} سرور…";
                });
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    profile.LatencyMs = -1;
                    completed++;
                    ProbeProgress.Value = completed * 100.0 / candidates.Count;
                    ProbeStatus.Text = $"در حال بررسی {completed} از {candidates.Count} سرور…";
                    AppendLog($"Probe failed for {profile.Name}: {ex.Message}");
                });
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks);
        var best = candidates
            .Where(p => p.LatencyMs >= 0)
            .OrderBy(p => p.LatencyMs)
            .ThenBy(p => p.Id == ProfileStore.Instance.State.ActiveProfileId ? 0 : 1)
            .FirstOrDefault();

        return best;
    }

    private void StartOrbAnimation()
    {
        if (!IsLoaded) return;
        if (ConnectButton.Template.FindName("OrbPulse", ConnectButton) is not FrameworkElement halo ||
            halo.RenderTransform is not ScaleTransform scale)
            return;

        var pulse = new System.Windows.Media.Animation.DoubleAnimation(
            1.0, 1.14, TimeSpan.FromSeconds(1.15))
        {
            AutoReverse = true,
            RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever,
            EasingFunction = new System.Windows.Media.Animation.SineEase
            {
                EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut
            }
        };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, pulse);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, pulse);
        halo.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(
            0.26, 0.62, TimeSpan.FromSeconds(1.15))
        {
            AutoReverse = true,
            RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever
        });
    }

    private void StopOrbAnimation()
    {
        if (ConnectButton?.Template?.FindName("OrbPulse", ConnectButton) is not FrameworkElement halo)
            return;
        if (halo.RenderTransform is ScaleTransform scale)
        {
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            scale.ScaleX = 1;
            scale.ScaleY = 1;
        }
        halo.BeginAnimation(OpacityProperty, null);
        halo.Opacity = 0.42;
    }

    private void ManualProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_connectionBusy || _manualScanBusy || _cloudSyncRunning)
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
        if (_connectionBusy || _manualScanBusy || _cloudSyncRunning)
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
        if (_connectionBusy || _manualScanBusy || _cloudSyncRunning)
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
        if (_connectionBusy || _manualScanBusy || _cloudSyncRunning)
        {
            MessageBox.Show("تا پایان عملیات جاری، فهرست سرورها را تغییر نده.",
                "عملیات در حال اجرا", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (_selectedProfile is null) return;
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
        if (Profiles.Count == 0 || _connectionBusy || _manualScanBusy || _cloudSyncRunning || _connection.IsRunning) return;
        var button = sender as Button;
        if (button is not null) button.IsEnabled = false;
        _manualScanBusy = true;
        _manualScanCts = new CancellationTokenSource();
        ProbeProgress.Visibility = Visibility.Visible;
        ProbeProgress.Value = 0;
        ProbeStatus.Text = "در حال تست واقعی مسیر سرورها…";
        StartOrbAnimation();
        try
        {
            var best = await ProbeAllProfilesAsync(_manualScanCts.Token);
            if (best is not null)
            {
                ProfilesList.SelectedItem = best;
                ProbeStatus.Text = $"بهترین سرور: {best.Name} · {best.LatencyMs} ms";
            }
            else
            {
                ProbeStatus.Text = "سرور پاسخ‌گو پیدا نشد";
                MessageBox.Show("هیچ سروری پاسخ HTTPS معتبر نداد.", "تست سرورها",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            SaveState();
        }
        catch (OperationCanceledException)
        {
            ProbeStatus.Text = "تست لغو شد";
        }
        finally
        {
            _manualScanBusy = false;
            _manualScanCts?.Dispose();
            _manualScanCts = null;
            ProbeProgress.Visibility = Visibility.Collapsed;
            StopOrbAnimation();
            if (button is not null) button.IsEnabled = true;
            if (_closeAfterBusyOperation)
            {
                _closeAfterBusyOperation = false;
                Close();
            }
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
        if (_connectionBusy || _manualScanBusy || _cloudSyncRunning || _subscriptionRefreshRunning)
        {
            MessageBox.Show("هنگام تست یا اتصال، اشتراک‌ها به‌روزرسانی نمی‌شوند.",
                "عملیات در حال اجرا", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (_selectedSubscription is null)
        {
            MessageBox.Show("ابتدا یک اشتراک انتخاب کن.", "اشتراک", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _subscriptionRefreshRunning = true;
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
        if (_cloudSyncRunning || _subscriptionRefreshRunning) return;
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
