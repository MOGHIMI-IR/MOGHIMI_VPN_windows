$ErrorActionPreference = "Stop"

$integrationRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $integrationRoot
$upstreamRoot = Join-Path $repoRoot "vendor\v2rayN"
$serviceSource = Join-Path $integrationRoot "MoghimiDeviceService.cs"

function Replace-Exact {
    param([string]$Path, [string]$Old, [string]$New, [string]$Label)
    $text = [System.IO.File]::ReadAllText($Path)
    $count = [regex]::Matches($text, [regex]::Escape($Old)).Count
    if ($count -ne 1) { throw "Patch anchor '$Label' expected exactly once in '$Path' but found $count." }
    $text = $text.Replace($Old, $New)
    [System.IO.File]::WriteAllText($Path, $text, [System.Text.UTF8Encoding]::new($false))
}

function Replace-Within-Section {
    param(
        [string]$Path,
        [string]$SectionStart,
        [string]$SectionEnd,
        [string]$Old,
        [string]$New,
        [string]$Label
    )
    $text = [System.IO.File]::ReadAllText($Path)
    $start = $text.IndexOf($SectionStart, [System.StringComparison]::Ordinal)
    if ($start -lt 0) { throw "Section start '$SectionStart' missing for patch '$Label'." }
    $end = $text.IndexOf($SectionEnd, $start + $SectionStart.Length, [System.StringComparison]::Ordinal)
    if ($end -lt 0) { $end = $text.Length }
    $section = $text.Substring($start, $end - $start)
    $count = [regex]::Matches($section, [regex]::Escape($Old)).Count
    if ($count -ne 1) { throw "Patch anchor '$Label' expected once within section but found $count." }
    $section = $section.Replace($Old, $New)
    $text = $text.Substring(0, $start) + $section + $text.Substring($end)
    [System.IO.File]::WriteAllText($Path, $text, [System.Text.UTF8Encoding]::new($false))
}

$packagesPath = Join-Path $upstreamRoot "v2rayN\Directory.Packages.props"
$packageVersionOld = '    <PackageVersion Include="ReactiveUI.WPF" Version="27.0.1" />'
$packageVersionNew = @'
    <PackageVersion Include="ReactiveUI.WPF" Version="27.0.1" />
    <PackageVersion Include="System.Security.Cryptography.ProtectedData" Version="10.0.15" />
'@
Replace-Exact $packagesPath $packageVersionOld $packageVersionNew "pin DPAPI ProtectedData package version"

$serviceProjectPath = Join-Path $upstreamRoot "v2rayN\ServiceLib\ServiceLib.csproj"
$packageRefOld = '<PackageReference Include="sqlite-net-e" />'
$packageRefNew = @'
<PackageReference Include="sqlite-net-e" />
        <PackageReference Include="System.Security.Cryptography.ProtectedData" />
'@
Replace-Exact $serviceProjectPath $packageRefOld $packageRefNew "reference DPAPI package from ServiceLib"

$serviceTarget = Join-Path $upstreamRoot "v2rayN\ServiceLib\Manager\MoghimiDeviceService.cs"
Copy-Item $serviceSource $serviceTarget -Force

$vmPath = Join-Path $upstreamRoot "v2rayN\ServiceLib\ViewModels\MainWindowViewModel.cs"
$vmOld = @'
        TaskManager.Instance.RegUpdateTask(_config, UpdateTaskHandler);

        if (_config.GuiItem.EnableStatistics || _config.GuiItem.DisplayRealTimeSpeed)
'@
$vmNew = @'
        TaskManager.Instance.RegUpdateTask(_config, UpdateTaskHandler);

        // The Moghimi quota service consumes the core's proxy-only traffic counters.
        _config.GuiItem.EnableStatistics = true;
        _config.GuiItem.DisplayRealTimeSpeed = true;
        try
        {
            await MoghimiDeviceService.Instance.InitializeAsync();
        }
        catch (Exception ex)
        {
            // Keep the GUI available while offline; CoreManager performs a strict
            // entitlement check before any real connection is allowed to start.
            Logging.SaveLog("MoghimiDeviceService.Initialize", ex);
        }

        if (_config.GuiItem.EnableStatistics || _config.GuiItem.DisplayRealTimeSpeed)
'@
Replace-Exact $vmPath $vmOld $vmNew "initialize Moghimi quota + force core statistics"

$statsPath = Join-Path $upstreamRoot "v2rayN\ServiceLib\Manager\StatisticsManager.cs"
$statsOld = @'
        if (server.ProxyUp != 0 || server.ProxyDown != 0)
        {
            _serverStatItem.TodayUp += server.ProxyUp;
'@
$statsNew = @'
        if (server.ProxyUp != 0 || server.ProxyDown != 0)
        {
            // These counters are proxy-only (not direct/system traffic).
            MoghimiDeviceService.Instance.AccumulateTraffic(server.ProxyDown, server.ProxyUp);
            _serverStatItem.TodayUp += server.ProxyUp;
'@
Replace-Exact $statsPath $statsOld $statsNew "report proxy traffic to Moghimi quota service"

$corePath = Join-Path $upstreamRoot "v2rayN\ServiceLib\Manager\CoreManager.cs"
$coreOld = @'
        if (mainContext == null)
        {
            await UpdateFunc(false, ResUI.CheckServerSettings);
            return;
        }

        var node = mainContext.Node;
'@
$coreNew = @'
        if (mainContext == null)
        {
            await UpdateFunc(false, ResUI.CheckServerSettings);
            return;
        }

        // Block every connection unless the server confirms free or paid entitlement.
        try
        {
            if (!await MoghimiDeviceService.Instance.EnsureAccessAllowedAsync())
            {
                await UpdateFunc(true, "حجم رایگان و اشتراک این دستگاه تمام شده است.");
                await CoreStop();
                await SysProxyHandler.UpdateSysProxy(_config, forceDisable: true);
                return;
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog("MoghimiDeviceService.EntitlementCheck", ex);
            await UpdateFunc(true, "اعتبار این دستگاه بررسی نشد؛ برای جلوگیری از مصرف ثبت‌نشده اتصال برقرار نشد.");
            await CoreStop();
            await SysProxyHandler.UpdateSysProxy(_config, forceDisable: true);
            return;
        }

        var node = mainContext.Node;
'@
Replace-Exact $corePath $coreOld $coreNew "enforce entitlement before core launch"

$appManagerPath = Join-Path $upstreamRoot "v2rayN\ServiceLib\Manager\AppManager.cs"
$shutdownOld = @'
            await StatisticsManager.Instance.SaveTo();
            await CoreManager.Instance.CoreStop();
            StatisticsManager.Instance.Close();
'@
$shutdownNew = @'
            await StatisticsManager.Instance.SaveTo();
            await CoreManager.Instance.CoreStop();
            await MoghimiDeviceService.Instance.FlushUsageAsync();
            StatisticsManager.Instance.Close();
'@
Replace-Exact $appManagerPath $shutdownOld $shutdownNew "flush pending usage on application exit"

$profilesVmPath = Join-Path $upstreamRoot "v2rayN\ServiceLib\ViewModels\ProfilesViewModel.cs"
$profilesOld = @'
        var canEditRemove = this.WhenAnyValue(
           x => x.SelectedProfile,
           selectedSource => selectedSource != null && !selectedSource.IndexId.IsNullOrEmpty());
'@
$profilesNew = @'
        var canUseProfile = this.WhenAnyValue(
           x => x.SelectedProfile,
           selectedSource => selectedSource != null && !selectedSource.IndexId.IsNullOrEmpty());

        // Moghimi cloud profiles remain connectable, but cannot be edited, moved,
        // copied, shared, exported, or deleted from the client.
        var canEditRemove = this.WhenAnyValue(
           x => x.SelectedProfile,
           selectedSource => selectedSource != null &&
               !selectedSource.IndexId.IsNullOrEmpty() &&
               selectedSource.Subid != "moghimi-cloud");

        var canEditSub = this.WhenAnyValue(
           x => x.SelectedSub,
           selectedSource => selectedSource != null &&
               !selectedSource.Id.IsNullOrEmpty() &&
               selectedSource.Id != "moghimi-cloud");
'@
Replace-Exact $profilesVmPath $profilesOld $profilesNew "separate cloud profile connect and edit permissions"

$setDefaultOld = @'
        SetDefaultServerCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await SetDefaultServer();
        }, canEditRemove);
'@
$setDefaultNew = @'
        SetDefaultServerCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await SetDefaultServer();
        }, canUseProfile);
'@
Replace-Exact $profilesVmPath $setDefaultOld $setDefaultNew "keep cloud profiles connectable"

$moveGroupOld = @'
        MoveToGroupCmd = ReactiveCommand.CreateFromTask<SubItem>(async sub =>
        {
            SelectedMoveToGroup = sub;
        });
'@
$moveGroupNew = @'
        MoveToGroupCmd = ReactiveCommand.CreateFromTask<SubItem>(async sub =>
        {
            SelectedMoveToGroup = sub;
        }, canEditRemove);
'@
Replace-Exact $profilesVmPath $moveGroupOld $moveGroupNew "prevent moving protected cloud profiles"

$editSubOld = @'
        EditSubCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await EditSubAsync(false);
        });
        DeleteSubCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await DeleteSubAsync();
        });
'@
$editSubNew = @'
        EditSubCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await EditSubAsync(false);
        }, canEditSub);
        DeleteSubCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await DeleteSubAsync();
        }, canEditSub);
'@
Replace-Exact $profilesVmPath $editSubOld $editSubNew "protect the Moghimi cloud group from edit/delete"

$updateVmPath = Join-Path $upstreamRoot "v2rayN\ServiceLib\ViewModels\CheckUpdateViewModel.cs"
$upstreamUpdateGuardOld = 'if (coreType == _v2rayN && Utils.IsPackagedInstall())'
$upstreamUpdateGuardNew = 'if (coreType == _v2rayN)'
Replace-Exact $updateVmPath $upstreamUpdateGuardOld $upstreamUpdateGuardNew "prevent upstream app self-updates"

$upstreamUpdateOld = @'
            else if (item.CoreType == _v2rayN)
            {
                if (Utils.IsPackagedInstall())
                {
                    await UpdateView(_v2rayN, ResUI.MsgNotSupport);
                    continue;
                }
                await CheckUpdateN(item.IsCheckPreRelease);
            }
'@
$upstreamUpdateNew = @'
            else if (item.CoreType == _v2rayN)
            {
                // The Moghimi fork must only update from its own signed release channel.
                await UpdateView(_v2rayN, "بروزرسانی برنامه از مسیر انتشار Moghimi انجام می‌شود.");
                continue;
            }
'@
Replace-Exact $updateVmPath $upstreamUpdateOld $upstreamUpdateNew "disable upstream GUI updater"

$duplicateOld = @'
        RemoveDuplicateServerCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await RemoveDuplicateServer();
        });
'@
$duplicateNew = @'
        RemoveDuplicateServerCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await RemoveDuplicateServer();
        }, canEditSub);
'@
Replace-Exact $profilesVmPath $duplicateOld $duplicateNew "protect cloud profiles from bulk deduplication"

$invalidOld = @'
        RemoveInvalidServerResultCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await RemoveInvalidServerResult();
        });
'@
$invalidNew = @'
        RemoveInvalidServerResultCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await RemoveInvalidServerResult();
        }, canEditSub);
'@
Replace-Exact $profilesVmPath $invalidOld $invalidNew "protect cloud profiles from bulk invalid-node removal"

$groupAllOld = @'
        GenGroupAllServerCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await GenGroupAllServer();
        }, canEditRemove);
        GenGroupRegionServerCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await GenGroupRegionServer();
        }, canEditRemove);
'@
$groupAllNew = @'
        GenGroupAllServerCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await GenGroupAllServer();
        }, canEditSub);
        GenGroupRegionServerCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await GenGroupRegionServer();
        }, canEditSub);
'@
Replace-Exact $profilesVmPath $groupAllOld $groupAllNew "prevent cloning protected cloud profiles into exportable groups"


$appManagerPath = Join-Path $upstreamRoot "v2rayN\ServiceLib\Manager\AppManager.cs"
$languageOld = '        Thread.CurrentThread.CurrentUICulture = new(CurrentConfig.UiItem.CurrentLanguage);'
$languageNew = @'
        CurrentConfig.UiItem.CurrentLanguage = "fa";
        Thread.CurrentThread.CurrentUICulture = new("fa");
'@
Replace-Exact $appManagerPath $languageOld $languageNew "set Persian UI culture"

$configFontsPath = Join-Path $upstreamRoot "v2rayN\v2rayN\Converters\MaterialDesignFonts.cs"
$fontOld = '        MyFont ??= new FontFamily("Microsoft YaHei");'
$fontNew = '        MyFont ??= new FontFamily("Tahoma");'
Replace-Exact $configFontsPath $fontOld $fontNew "use Persian-capable fallback font"

$appPath = Join-Path $upstreamRoot "v2rayN\v2rayN\App.xaml"
$appOld = @'
                    BaseTheme="Light"
                    PrimaryColor="Blue"
                    SecondaryColor="Lime" />
'@
$appNew = @'
                    BaseTheme="Dark"
                    PrimaryColor="DeepPurple"
                    SecondaryColor="Cyan" />
'@
Replace-Exact $appPath $appOld $appNew "switch to dark violet cyan palette"

$paperOld = @'
            <system:Double x:Key="MenuItemHeight">32</system:Double>
'@
$paperNew = @'
            <LinearGradientBrush x:Key="MoghimiWindowGradient" StartPoint="0,0" EndPoint="1,1">
                <GradientStop Color="#FF080D1A" Offset="0" />
                <GradientStop Color="#FF121A30" Offset="0.52" />
                <GradientStop Color="#FF101124" Offset="1" />
            </LinearGradientBrush>
            <system:Double x:Key="MenuItemHeight">32</system:Double>
'@
Replace-Exact $appPath $paperOld $paperNew "define Moghimi glass dark background"

$windowBackgroundOld = @'
                <Setter Property="Background" Value="{DynamicResource MaterialDesignPaper}" />
'@
$windowBackgroundNew = @'
                <Setter Property="Background" Value="{StaticResource MoghimiWindowGradient}" />
'@
Replace-Within-Section $appPath 'x:Key="WindowGlobal"' 'x:Key="ViewGlobal"' $windowBackgroundOld $windowBackgroundNew "apply Moghimi gradient background"

$mainXamlPath = Join-Path $upstreamRoot "v2rayN\v2rayN\Views\MainWindow.xaml"
$titleOld = '    Title="v2rayN"'
$titleNew = @'
    Title="Moghimi VPN | Windows"
    FlowDirection="RightToLeft"
'@
Replace-Exact $mainXamlPath $titleOld $titleNew "brand app window title"




$gridOld = @'
        <Grid>
            <DockPanel>
'@
$gridNew = @'
        <Grid ClipToBounds="True">
            <Ellipse x:Name="MoghimiGlowLeft"
                     Width="430" Height="430"
                     HorizontalAlignment="Left" VerticalAlignment="Top"
                     Margin="-170,-190,0,0"
                     Fill="#553C6BFF" Opacity="0.18"
                     IsHitTestVisible="False">
                <Ellipse.Effect>
                    <BlurEffect Radius="90" />
                </Ellipse.Effect>
            </Ellipse>
            <Ellipse x:Name="MoghimiGlowRight"
                     Width="500" Height="500"
                     HorizontalAlignment="Right" VerticalAlignment="Bottom"
                     Margin="0,0,-220,-260"
                     Fill="#554B2AAE" Opacity="0.16"
                     IsHitTestVisible="False">
                <Ellipse.Effect>
                    <BlurEffect Radius="110" />
                </Ellipse.Effect>
            </Ellipse>
            <DockPanel>
'@
Replace-Exact $mainXamlPath $gridOld $gridNew "add ambient motion glow layers"

# Wrap the main application content in one translucent glass surface.
$mainWindowText = [System.IO.File]::ReadAllText($mainXamlPath)
$gridMarker = '<Grid ClipToBounds="True">'
$gridAt = $mainWindowText.IndexOf($gridMarker, [System.StringComparison]::Ordinal)
$dialogEnd = $mainWindowText.IndexOf('</materialDesign:DialogHost>', $gridAt, [System.StringComparison]::Ordinal)
if ($gridAt -lt 0 -or $dialogEnd -lt 0) { throw "Main window root content could not be located for the glass surface." }
$rootSection = $mainWindowText.Substring($gridAt, $dialogEnd - $gridAt)
if (-not $rootSection.Contains('x:Name="MoghimiGlassBorder"')) {
    $dockStart = $rootSection.IndexOf('<DockPanel>', [System.StringComparison]::Ordinal)
    $dockEnd = $rootSection.LastIndexOf('</DockPanel>', [System.StringComparison]::Ordinal)
    if ($dockStart -lt 0 -or $dockEnd -lt $dockStart) { throw "Main DockPanel was not found for the glass surface." }
    $glassOpen = @'
<Border x:Name="MoghimiGlassBorder"
        Margin="12"
        CornerRadius="20"
        BorderThickness="1"
        BorderBrush="#667DA8FF"
        Background="#1BFFFFFF">
    <Border.Effect>
        <DropShadowEffect BlurRadius="26" ShadowDepth="0" Opacity="0.28" Color="#FF5C55C9" />
    </Border.Effect>
'@
    $glassClose = '</Border>'
    $rootSection = $rootSection.Insert($dockEnd + '</DockPanel>'.Length, $glassClose)
    $rootSection = $rootSection.Insert($dockStart, $glassOpen)
    $mainWindowText = $mainWindowText.Substring(0, $gridAt) + $rootSection + $mainWindowText.Substring($dialogEnd)
    [System.IO.File]::WriteAllText($mainXamlPath, $mainWindowText, [System.Text.UTF8Encoding]::new($false))
}

$sizeOld = @'
    Width="1200"
    Height="800"
    MinWidth="800"
'@
$sizeNew = @'
    Width="1360"
    Height="860"
    MinWidth="980"
    MinHeight="680"
'@
Replace-Exact $mainXamlPath $sizeOld $sizeNew "set premium desktop window dimensions"

$toolbarAnchor = @'
                        <materialDesign:PopupBox
'@
$toolbarInsert = @'
                        <Border Margin="3,0,6,0"
                                Padding="9,5"
                                CornerRadius="11"
                                Background="#331D2A49"
                                BorderBrush="#6678A7FF"
                                BorderThickness="1"
                                VerticalAlignment="Center">
                            <StackPanel Orientation="Horizontal" VerticalAlignment="Center">
                                <StackPanel Margin="0,0,8,0">
                                    <TextBlock Text="سهمیه دستگاه" FontSize="10" Foreground="#FFB7C9E8" />
                                    <ProgressBar x:Name="pbMoghimiFreeQuota"
                                                 Width="92" Height="7"
                                                 Minimum="0" Maximum="100" Value="0"
                                                 Background="#443C4B69"
                                                 Foreground="#FF4DD9E8"
                                                 BorderThickness="0" />
                                </StackPanel>
                                <TextBlock x:Name="txtMoghimiQuota"
                                           VerticalAlignment="Center"
                                           Foreground="White"
                                           FontWeight="SemiBold"
                                           Text="۸ گیگ رایگان" />
                            </StackPanel>
                        </Border>
                        <Button x:Name="btnMoghimiSync"
                                Margin="3,0"
                                Padding="12,7"
                                Background="#663B2C77"
                                Foreground="White"
                                BorderBrush="#887E8BFF"
                                BorderThickness="1"
                                ToolTip="دریافت و به‌روزرسانی سرورهای مقیمی">
                            <StackPanel Orientation="Horizontal">
                                <materialDesign:PackIcon Kind="Sync" Margin="0,0,6,0" VerticalAlignment="Center" />
                                <TextBlock Text="همگام‌سازی مقیمی" />
                            </StackPanel>
                        </Button>
                        <Button x:Name="btnMoghimiActivate"
                                Margin="3,0"
                                Padding="12,7"
                                Background="#3323A6A9"
                                Foreground="White"
                                BorderBrush="#6670E6E8"
                                BorderThickness="1"
                                ToolTip="ثبت کد فعال‌سازی اشتراک ویندوز">
                            <StackPanel Orientation="Horizontal">
                                <materialDesign:PackIcon Kind="VpnKey" Margin="0,0,6,0" VerticalAlignment="Center" />
                                <TextBlock Text="فعال‌سازی اشتراک" />
                            </StackPanel>
                        </Button>
                        <materialDesign:PopupBox
'@
Replace-Exact $mainXamlPath $toolbarAnchor $toolbarInsert "add quota sync and activation controls"

$mainCsPath = Join-Path $upstreamRoot "v2rayN\v2rayN\Views\MainWindow.xaml.cs"
$usingOld = @'
using System.Windows.Controls;
using System.Windows.Media;
'@
$usingNew = @'
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
'@
Replace-Exact $mainCsPath $usingOld $usingNew "add WPF dialog and animation namespaces"

$ctorOld = @'
        InitializeComponent();

        _config = ConfigTextHandler.Config;
'@
$ctorNew = @'
        InitializeComponent();

        Title = $"Moghimi VPN | {Utils.GetVersion()}";
        btnMoghimiSync.Click += BtnMoghimiSync_Click;
        btnMoghimiActivate.Click += BtnMoghimiActivate_Click;
        MoghimiDeviceService.Instance.StatusChanged += MoghimiDeviceService_StatusChanged;
        Loaded += (_, _) => StartMoghimiMotion();

        _config = ConfigTextHandler.Config;
'@
Replace-Exact $mainCsPath $ctorOld $ctorNew "wire Moghimi toolbar and motion"

$methods = @'

    private void StartMoghimiMotion()
    {
        var pulseLeft = new DoubleAnimation(0.12, 0.28, TimeSpan.FromSeconds(5))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever
        };
        var pulseRight = new DoubleAnimation(0.10, 0.25, TimeSpan.FromSeconds(6))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever
        };
        var drift = new DoubleAnimation(-28, 28, TimeSpan.FromSeconds(12))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever
        };
        var glassPulse = new DoubleAnimation(0.94, 1.0, TimeSpan.FromSeconds(4.5))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever
        };

        MoghimiGlowLeft.BeginAnimation(OpacityProperty, pulseLeft);
        MoghimiGlowRight.BeginAnimation(OpacityProperty, pulseRight);
        MoghimiGlassBorder.BeginAnimation(OpacityProperty, glassPulse);
        MoghimiGlowRight.RenderTransform = new TranslateTransform();
        MoghimiGlowRight.RenderTransform.BeginAnimation(TranslateTransform.XProperty, drift);
    }

    private void MoghimiDeviceService_StatusChanged(object? sender, MoghimiDeviceStatus status)
    {
        Dispatcher.BeginInvoke(new Action(() => UpdateMoghimiUsageUI(status)));
    }

    private void UpdateMoghimiUsageUI(MoghimiDeviceStatus status)
    {
        pbMoghimiFreeQuota.Value = status.FreePercent;
        var free = status.FreeRemainingBytes / 1073741824d;
        var paid = status.PaidRemainingBytes / 1073741824d;
        txtMoghimiQuota.Text = $"رایگان {free:0.00} گیگ | اشتراک {paid:0.00} گیگ";
    }

    private async void BtnMoghimiSync_Click(object sender, RoutedEventArgs e)
    {
        btnMoghimiSync.IsEnabled = false;
        try
        {
            var imported = await MoghimiDeviceService.Instance.SyncProfilesAsync();
            await ViewModel.ProfilesViewModel.RefreshSubscriptions();
            await ViewModel.ProfilesViewModel.RefreshServersBiz();
            await ViewModel.StatusBarViewModel.RefreshServersBiz();
            NoticeManager.Instance.Enqueue($"همگام‌سازی انجام شد؛ {imported} سرور مقیمی به‌روزرسانی شد.");
            var status = await MoghimiDeviceService.Instance.GetStatusAsync(forceRefresh: true);
            UpdateMoghimiUsageUI(status);
        }
        catch (Exception ex)
        {
            Logging.SaveLog("MoghimiSync", ex);
            NoticeManager.Instance.Enqueue(ex.Message);
        }
        finally
        {
            btnMoghimiSync.IsEnabled = true;
        }
    }

    private void BtnMoghimiActivate_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Window
        {
            Title = "فعال‌سازی اشتراک مقیمی",
            Width = 470,
            Height = 245,
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            Background = new SolidColorBrush(Color.FromRgb(16, 23, 43)),
            Foreground = Brushes.White,
            ShowInTaskbar = false
        };

        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock
        {
            Text = "کد فعال‌سازی نسخه ویندوز را وارد کن",
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
            Margin = new Thickness(0, 0, 0, 12)
        });

        var keyBox = new TextBox
        {
            Height = 42,
            Padding = new Thickness(10, 8, 10, 8),
            FontSize = 15,
            FlowDirection = FlowDirection.LeftToRight,
            TextAlignment = TextAlignment.Center,
            ToolTip = "کد فعال‌سازی ۳۲ کاراکتری"
        };
        panel.Children.Add(keyBox);

        var activate = new Button
        {
            Content = "فعال‌سازی اشتراک",
            Margin = new Thickness(0, 18, 0, 0),
            Padding = new Thickness(18, 9, 18, 9),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Background = new SolidColorBrush(Color.FromRgb(74, 58, 137)),
            Foreground = Brushes.White
        };
        panel.Children.Add(activate);
        activate.Click += async (_, _) =>
        {
            activate.IsEnabled = false;
            try
            {
                await MoghimiDeviceService.Instance.ActivateAsync(keyBox.Text);
                var status = await MoghimiDeviceService.Instance.GetStatusAsync(forceRefresh: true);
                UpdateMoghimiUsageUI(status);
                NoticeManager.Instance.Enqueue("اشتراک ویندوز با موفقیت فعال شد.");
                dialog.Close();
            }
            catch (Exception ex)
            {
                Logging.SaveLog("MoghimiActivation", ex);
                MessageBox.Show(dialog, ex.Message, "خطا در فعال‌سازی", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                activate.IsEnabled = true;
            }
        };

        dialog.Content = panel;
        dialog.ShowDialog();
    }
'@
$csText = [System.IO.File]::ReadAllText($mainCsPath)
if ($csText.Contains("private void StartMoghimiMotion()")) {
    throw "Moghimi MainWindow handlers are already present; refusing to duplicate the patch."
}
$lastBrace = $csText.LastIndexOf("}")
if ($lastBrace -lt 0) { throw "MainWindow class closing brace was not found." }
$csText = $csText.Insert($lastBrace, $methods + [Environment]::NewLine)
[System.IO.File]::WriteAllText($mainCsPath, $csText, [System.Text.UTF8Encoding]::new($false))


function New-MoghimiIcon {
    Add-Type -AssemblyName System.Drawing
    $iconPath = Join-Path $upstreamRoot "v2rayN\v2rayN\Resources\v2rayN.ico"
    $pngPath = Join-Path ([System.IO.Path]::GetTempPath()) ("moghimi-icon-" + [Guid]::NewGuid().ToString("N") + ".png")
    $bitmap = [System.Drawing.Bitmap]::new(256, 256, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    try {
        $graphics.Clear([System.Drawing.Color]::Transparent)
        $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
        $path.AddArc(12, 12, 48, 48, 180, 90)
        $path.AddArc(196, 12, 48, 48, 270, 90)
        $path.AddArc(196, 196, 48, 48, 0, 90)
        $path.AddArc(12, 196, 48, 48, 90, 90)
        $path.CloseFigure()
        $bg = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
            [System.Drawing.Rectangle]::new(0, 0, 256, 256),
            [System.Drawing.Color]::FromArgb(255, 8, 14, 30),
            [System.Drawing.Color]::FromArgb(255, 39, 28, 84),
            35
        )
        $graphics.FillPath($bg, $path)
        $rim = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(220, 92, 199, 255), 4)
        $graphics.DrawPath($rim, $path)

        $shield = [System.Drawing.Point[]]@(
            [System.Drawing.Point]::new(128, 38),
            [System.Drawing.Point]::new(194, 65),
            [System.Drawing.Point]::new(184, 142),
            [System.Drawing.Point]::new(128, 202),
            [System.Drawing.Point]::new(72, 142),
            [System.Drawing.Point]::new(62, 65)
        )
        $shieldFill = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(78, 76, 207, 255))
        $graphics.FillPolygon($shieldFill, $shield)
        $shieldPen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(230, 111, 213, 255), 5)
        $graphics.DrawPolygon($shieldPen, $shield)

        $mPen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(255, 238, 246, 255), 17)
        $mPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $mPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        $mPen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
        $m = [System.Drawing.Point[]]@(
            [System.Drawing.Point]::new(91, 145),
            [System.Drawing.Point]::new(91, 96),
            [System.Drawing.Point]::new(128, 132),
            [System.Drawing.Point]::new(165, 96),
            [System.Drawing.Point]::new(165, 145)
        )
        $graphics.DrawLines($mPen, $m)
        $accent = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(255, 67, 229, 225), 7)
        $accent.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $accent.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        $graphics.DrawLine($accent, 109, 166, 147, 166)

        $bitmap.Save($pngPath, [System.Drawing.Imaging.ImageFormat]::Png)
        $png = [System.IO.File]::ReadAllBytes($pngPath)
        $stream = [System.IO.File]::Create($iconPath)
        $writer = [System.IO.BinaryWriter]::new($stream)
        try {
            $writer.Write([UInt16]0)
            $writer.Write([UInt16]1)
            $writer.Write([UInt16]1)
            $writer.Write([byte]0)
            $writer.Write([byte]0)
            $writer.Write([byte]0)
            $writer.Write([byte]0)
            $writer.Write([UInt16]1)
            $writer.Write([UInt16]32)
            $writer.Write([UInt32]$png.Length)
            $writer.Write([UInt32]22)
            $writer.Write($png)
        }
        finally {
            $writer.Dispose()
            $stream.Dispose()
        }
    }
    finally {
        $graphics.Dispose()
        $bitmap.Dispose()
        if (Test-Path $pngPath) { Remove-Item $pngPath -Force }
    }
}

New-MoghimiIcon

$packageScriptPath = Join-Path $upstreamRoot "package-windows.ps1"
$packageText = [System.IO.File]::ReadAllText($packageScriptPath)
$packageReplacements = @(
    @('v2rayN Setup', 'Moghimi VPN Setup'),
    @('v2rayN setup', 'Moghimi VPN setup'),
    @('v2rayN is installed', 'Moghimi VPN is installed'),
    @('You can repair the installation or uninstall v2rayN.', 'You can repair, update, or uninstall Moghimi VPN.'),
    @('<Label X="112" Y="29" Width="-32" Height="44" FontId="1" DisablePrefix="yes">v2rayN</Label>', '<Label X="112" Y="29" Width="-32" Height="44" FontId="1" DisablePrefix="yes">Moghimi VPN</Label>'),
    @('Manufacturer="2dust"', 'Manufacturer="Moghimi VPN"'),
    @('"v2rayN $flavorLabel ($arch)"', '"Moghimi VPN $flavorLabel ($arch)"'),
    @('$folderName = if ($flavor -eq ''wpf'') { ''v2rayN'' } else { ''v2rayN Desktop'' }', '$folderName = if ($flavor -eq ''wpf'') { ''Moghimi VPN'' } else { ''Moghimi VPN Desktop'' }'),
    @('$shortcutName = if ($flavor -eq ''wpf'') { ''v2rayN'' } else { ''v2rayN Desktop'' }', '$shortcutName = if ($flavor -eq ''wpf'') { ''Moghimi VPN'' } else { ''Moghimi VPN Desktop'' }'),
    @('"Software\v2rayN\Installer\$flavor\$arch"', '"Software\MoghimiVPN\Installer\$flavor\$arch"'),
    @('"v2rayN-windows-$archSuffix$desktopSuffix.exe"', '"MoghimiVPN-windows-$archSuffix$desktopSuffix.exe"')
)
foreach ($pair in $packageReplacements) {
    $oldValue = [string]$pair[0]
    $newValue = [string]$pair[1]
    if ($packageText.Contains($oldValue)) {
        $packageText = $packageText.Replace($oldValue, $newValue)
    }
}
[System.IO.File]::WriteAllText($packageScriptPath, $packageText, [System.Text.UTF8Encoding]::new($false))

Write-Host "Moghimi integration patches applied to pinned v2rayN source."
