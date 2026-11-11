using System.Diagnostics;
using BatteryTray.Core;
using BatteryTray.Core.Settings;
using BatteryTray.Interop;
using BatteryTray.Services;
using BatteryTray.Windows.Rendering;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.System;
using WinRT.Interop;

namespace BatteryTray.Views;

/// <summary>Ventana de ajustes con el aspecto de la app Configuración de Windows 11.</summary>
public sealed partial class SettingsWindow : Window
{
    private readonly AppController _controller;
    private readonly Dictionary<string, SettingsCard> _deviceCards = new(StringComparer.Ordinal);
    private string _deviceSignature = "";
    private bool _loading = true;

    internal SettingsWindow(AppController controller)
    {
        _controller = controller;
        InitializeComponent();

        Title = $"{Loc.Get("AppDisplayName")} · {Loc.Get("SettingsTitle")}";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon(AppInfo.IconPath);
        SizeAndCenter();

        ApplyTexts();
        LoadValues();
        RefreshDevices();

        _controller.DevicesChanged += RefreshDevices;
        _controller.TaskbarBarChanged += UpdatePlacementState;
        Closed += (_, _) =>
        {
            _controller.DevicesChanged -= RefreshDevices;
            _controller.TaskbarBarChanged -= UpdatePlacementState;
        };
        _ = LoadStartupStateAsync();
        _loading = false;
    }

    private void SizeAndCenter()
    {
        nint hwnd = WindowNative.GetWindowHandle(this);
        double scale = Math.Max(1.0, NativeMethods.GetDpiForSystem() / 96.0);
        int w = (int)(820 * scale);
        int h = (int)(780 * scale);
        RectInt32 work = DisplayArea.Primary.WorkArea;
        w = Math.Min(w, work.Width);
        h = Math.Min(h, work.Height);
        AppWindow.MoveAndResize(new RectInt32(work.X + (work.Width - w) / 2, work.Y + (work.Height - h) / 2, w, h));
        _ = hwnd;
    }

    private void ApplyTexts()
    {
        TitleBarText.Text = Title;
        PageTitle.Text = Loc.Get("SettingsTitle");

        GeneralHeader.Text = Loc.Get("SectionGeneral");
        StartupCard.Header = Loc.Get("StartupHeader");
        StartupCard.Description = Loc.Get("StartupDesc");
        LanguageCard.Header = Loc.Get("LanguageHeader");
        LanguageCard.Description = Loc.Get("LanguageDesc");
        RestartBar.Title = Loc.Get("RestartTitle");
        RestartButton.Content = Loc.Get("RestartNow");

        TaskbarHeader.Text = Loc.Get("SectionTaskbar");
        PlacementCard.Header = Loc.Get("PlacementHeader");
        PlacementCard.Description = Loc.Get("PlacementDesc");
        BarUnavailableBar.Message = Loc.Get("BarUnavailable");
        TrayModeCard.Header = Loc.Get("TrayModeHeader");
        TrayModeCard.Description = Loc.Get("TrayModeDesc");
        IconStyleCard.Header = Loc.Get("IconStyleHeader");
        IconStyleCard.Description = Loc.Get("IconStyleDesc");
        PinCard.Header = Loc.Get("PinHeader");
        PinCard.Description = Loc.Get("PinDesc");
        PinButton.Content = Loc.Get("PinButton");

        NotificationsHeader.Text = Loc.Get("SectionNotifications");
        LowBatteryExpander.Header = Loc.Get("LowBatteryHeader");
        LowBatteryExpander.Description = Loc.Get("LowBatteryDesc");
        ThresholdCard.Header = Loc.Get("ThresholdHeader");
        ConnectionCard.Header = Loc.Get("ConnectionHeader");
        ConnectionCard.Description = Loc.Get("ConnectionDesc");

        DevicesHeader.Text = Loc.Get("SectionDevices");
        DevicesDesc.Text = Loc.Get("DevicesDesc");

        AdvancedHeader.Text = Loc.Get("SectionAdvanced");
        DemoCard.Header = Loc.Get("DemoHeader");
        DemoCard.Description = Loc.Get("DemoDesc");
        DiagCard.Header = Loc.Get("DiagHeader");
        DiagCard.Description = Loc.Get("DiagDesc");
        DiagButton.Content = Loc.Get("DiagButton");
        LogsCard.Header = Loc.Get("LogsHeader");
        LogsCard.Description = Loc.Get("LogsDesc");
        LogsButton.Content = Loc.Get("LogsButton");

        AboutHeader.Text = Loc.Get("SectionAbout");
        AboutCard.Header = $"{Loc.Get("AppDisplayName")} · {Loc.Format("AboutVersion", AppInfo.Version)}";
        AboutCard.Description = Loc.Get("AboutDesc");
        SourceLink.Content = Loc.Get("AboutSource");
        SourceLink.NavigateUri = new Uri(AppInfo.RepositoryUrl);

        foreach (ToggleSwitch toggle in new[] { StartupToggle, LowBatteryToggle, ConnectionToggle, DemoToggle })
        {
            toggle.OnContent = Loc.Get("On");
            toggle.OffContent = Loc.Get("Off");
        }

        LanguageCombo.Items.Add(new ComboBoxItem { Content = Loc.Get("LanguageAuto"), Tag = "auto" });
        LanguageCombo.Items.Add(new ComboBoxItem { Content = "Español", Tag = "es" });
        LanguageCombo.Items.Add(new ComboBoxItem { Content = "English", Tag = "en" });

        PlacementCombo.Items.Add(new ComboBoxItem { Content = Loc.Get("PlacementTray"), Tag = DisplayPlacement.NotificationArea });
        PlacementCombo.Items.Add(new ComboBoxItem { Content = Loc.Get("PlacementTaskbar"), Tag = DisplayPlacement.Taskbar });

        TrayModeCombo.Items.Add(new ComboBoxItem { Content = Loc.Get("TrayModePerDevice"), Tag = TrayMode.PerDevice });
        TrayModeCombo.Items.Add(new ComboBoxItem { Content = Loc.Get("TrayModeSingle"), Tag = TrayMode.Single });

        IconStyleCombo.Items.Add(new ComboBoxItem { Content = Loc.Get("IconStyleWindows"), Tag = IconStyle.WindowsBattery });
        IconStyleCombo.Items.Add(new ComboBoxItem { Content = Loc.Get("IconStylePercentage"), Tag = IconStyle.Percentage });
        IconStyleCombo.Items.Add(new ComboBoxItem { Content = Loc.Get("IconStyleDevice"), Tag = IconStyle.DeviceIcon });
    }

    private void LoadValues()
    {
        AppSettings s = _controller.Settings;
        SelectByTag(LanguageCombo, s.Language);
        SelectByTag(PlacementCombo, s.Placement);
        SelectByTag(TrayModeCombo, s.TrayMode);
        SelectByTag(IconStyleCombo, s.IconStyle);
        LowBatteryToggle.IsOn = s.NotifyLowBattery;
        ThresholdSlider.Value = s.LowBatteryThreshold;
        ThresholdValue.Text = Loc.Format("Percent", s.LowBatteryThreshold);
        ConnectionToggle.IsOn = s.NotifyConnectionChanges;
        DemoToggle.IsOn = s.DemoMode;
        UpdatePlacementState();
    }

    /// <summary>
    /// Con la barra en la barra de tareas, los ajustes que solo afectan a los iconos junto
    /// al reloj se desactivan; y si la barra no cabe, se explica por qué se ven los iconos.
    /// </summary>
    private void UpdatePlacementState()
    {
        bool taskbar = _controller.Settings.Placement == DisplayPlacement.Taskbar;
        bool barShown = taskbar && _controller.TaskbarBarActive;
        BarUnavailableBar.IsOpen = taskbar && !barShown;

        string? trayOnly = barShown ? Loc.Get("TrayOnlySetting") : null;
        TrayModeCard.IsEnabled = !barShown;
        TrayModeCard.Description = trayOnly ?? Loc.Get("TrayModeDesc");
        IconStyleCard.IsEnabled = !barShown;
        IconStyleCard.Description = trayOnly ?? Loc.Get("IconStyleDesc");
        PinCard.IsEnabled = !barShown;
        PinCard.Description = trayOnly ?? Loc.Get("PinDesc");
    }

    private static void SelectByTag(ComboBox combo, object value)
    {
        combo.SelectedItem = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => Equals(i.Tag, value)) ?? combo.Items.FirstOrDefault();
    }

    private async Task LoadStartupStateAsync()
    {
        StartupState state = await _controller.GetStartupStateAsync();
        ShowStartupState(state);
    }

    private void ShowStartupState(StartupState state)
    {
        bool previous = _loading;
        _loading = true;
        StartupToggle.IsOn = state == StartupState.Enabled;
        StartupToggle.IsEnabled = state != StartupState.ControlledByPolicy;
        StartupCard.Description = state == StartupState.DisabledByUser ? Loc.Get("StartupBlocked") : Loc.Get("StartupDesc");
        _loading = previous;
    }

    // ------------------------------------------------------------ dispositivos

    private void RefreshDevices()
    {
        AppSettings s = _controller.Settings;
        IReadOnlyList<BatteryDevice> devices = _controller.Devices;

        // Si solo cambian los niveles, se actualiza el texto sin rehacer las tarjetas
        // (así no se cierra un menú que el usuario tenga abierto).
        string signature = string.Join("|", devices.Select(d => $"{d.Id}:{s.DisplayName(d)}:{s.IsHidden(d.Id)}:{s.HasTrayIcon(d)}:{d.IsConnected}"));
        if (signature == _deviceSignature)
        {
            foreach (BatteryDevice d in devices)
            {
                if (_deviceCards.TryGetValue(d.Id, out SettingsCard? card))
                {
                    card.Description = DeviceDescription(d, s);
                }
            }

            return;
        }

        _deviceSignature = signature;
        _deviceCards.Clear();
        DevicesPanel.Children.Clear();

        if (devices.Count == 0)
        {
            DevicesPanel.Children.Add(new SettingsCard { Header = Loc.Get("DevicesEmpty") });
            return;
        }

        foreach (BatteryDevice d in devices)
        {
            SettingsCard card = BuildDeviceCard(d, s);
            _deviceCards[d.Id] = card;
            DevicesPanel.Children.Add(card);
        }
    }

    private static string DeviceDescription(BatteryDevice d, AppSettings s)
    {
        var parts = new List<string> { Loc.Kind(d.Kind) };
        if (d.Level is int level)
        {
            parts.Add(Loc.Format("Percent", level));
        }

        parts.Add(d.IsConnected && !d.IsStale ? Loc.Get("StatusConnected") : Loc.Get("StatusDisconnected"));
        if (s.IsHidden(d.Id))
        {
            parts.Add(Loc.Get("DeviceHidden"));
        }

        return string.Join(" · ", parts);
    }

    private SettingsCard BuildDeviceCard(BatteryDevice d, AppSettings s)
    {
        string id = d.Id;
        var headerIcon = new FontIcon { Glyph = DeviceGlyphs.For(d.Kind) };

        var card = new SettingsCard
        {
            Header = s.DisplayName(d),
            Description = DeviceDescription(d, s),
            HeaderIcon = headerIcon,
        };

        var show = new ToggleMenuFlyoutItem { Text = Loc.Get("DeviceShow"), IsChecked = !s.IsHidden(id) };
        show.Click += (_, _) => _controller.UpdateSettings(x =>
        {
            x.HiddenDevices.Remove(id);
            if (!show.IsChecked)
            {
                x.HiddenDevices.Add(id);
            }
        });

        var tray = new ToggleMenuFlyoutItem { Text = Loc.Get("DeviceTrayIcon"), IsChecked = s.HasTrayIcon(d) };
        tray.Click += (_, _) => _controller.UpdateSettings(x => x.TrayIconOverrides[id] = tray.IsChecked);

        var rename = new MenuFlyoutItem { Text = Loc.Get("DeviceRename"), Icon = new FontIcon { Glyph = "" } };
        rename.Click += async (_, _) => await RenameAsync(d);

        var forget = new MenuFlyoutItem
        {
            Text = Loc.Get("DeviceForget"),
            Icon = new FontIcon { Glyph = "" },
            IsEnabled = !d.IsConnected || d.IsStale,
        };
        forget.Click += (_, _) => _controller.Forget(id);

        var menu = new MenuFlyout();
        menu.Items.Add(show);
        menu.Items.Add(tray);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(rename);
        menu.Items.Add(forget);

        var button = new Button { Content = new FontIcon { Glyph = "", FontSize = 14 }, Flyout = menu };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, $"{Loc.Get("DeviceOptions")}: {s.DisplayName(d)}");
        ToolTipService.SetToolTip(button, Loc.Get("DeviceOptions"));
        card.Content = button;
        return card;
    }

    private async Task RenameAsync(BatteryDevice d)
    {
        AppSettings s = _controller.Settings;
        var box = new TextBox
        {
            Text = s.Aliases.TryGetValue(d.Id, out string? alias) ? alias : "",
            PlaceholderText = d.Name,
            MaxLength = DeviceText.MaxNameLength,
        };
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(box);
        panel.Children.Add(new TextBlock
        {
            Text = Loc.Get("RenamePlaceholder"),
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            TextWrapping = TextWrapping.Wrap,
        });

        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = Loc.Get("RenameTitle"),
            Content = panel,
            PrimaryButtonText = Loc.Get("Save"),
            CloseButtonText = Loc.Get("Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        // El nombre lo escribe el usuario: se limpia igual que los nombres de dispositivo.
        string clean = DeviceText.Sanitize(box.Text, "");
        _controller.UpdateSettings(x =>
        {
            if (clean.Length == 0)
            {
                x.Aliases.Remove(d.Id);
            }
            else
            {
                x.Aliases[d.Id] = clean;
            }
        });
    }

    // ------------------------------------------------------------ eventos

    private async void StartupToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        StartupState state = await _controller.SetStartupAsync(StartupToggle.IsOn);
        ShowStartupState(state);
    }

    private void LanguageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || LanguageCombo.SelectedItem is not ComboBoxItem { Tag: string tag })
        {
            return;
        }

        _controller.UpdateSettings(s => s.Language = tag);
        RestartBar.IsOpen = Loc.Resolve(tag) != Loc.LanguageTag;
    }

    private void RestartButton_Click(object sender, RoutedEventArgs e) => _controller.Restart();

    private void PlacementCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && PlacementCombo.SelectedItem is ComboBoxItem { Tag: DisplayPlacement placement })
        {
            _controller.UpdateSettings(s => s.Placement = placement);
            UpdatePlacementState();
        }
    }

    private void TrayModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && TrayModeCombo.SelectedItem is ComboBoxItem { Tag: TrayMode mode })
        {
            _controller.UpdateSettings(s => s.TrayMode = mode);
        }
    }

    private void IconStyleCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && IconStyleCombo.SelectedItem is ComboBoxItem { Tag: IconStyle style })
        {
            _controller.UpdateSettings(s => s.IconStyle = style);
        }
    }

    private async void PinButton_Click(object sender, RoutedEventArgs e) =>
        await Launcher.LaunchUriAsync(new Uri("ms-settings:taskbar"));

    private void LowBatteryToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_loading)
        {
            _controller.UpdateSettings(s => s.NotifyLowBattery = LowBatteryToggle.IsOn);
        }
    }

    private void ThresholdSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        int value = (int)Math.Round(e.NewValue);
        if (ThresholdValue is not null)
        {
            ThresholdValue.Text = Loc.Format("Percent", value);
        }

        if (!_loading)
        {
            _controller.UpdateSettings(s => s.LowBatteryThreshold = value);
        }
    }

    private void ConnectionToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_loading)
        {
            _controller.UpdateSettings(s => s.NotifyConnectionChanges = ConnectionToggle.IsOn);
        }
    }

    private void DemoToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_loading)
        {
            _deviceSignature = "";
            _controller.UpdateSettings(s => s.DemoMode = DemoToggle.IsOn, restartProviders: true);
        }
    }

    private async void DiagButton_Click(object sender, RoutedEventArgs e)
    {
        DiagButton.IsEnabled = false;
        try
        {
            string report = await _controller.BuildDiagnosticsAsync();
            var package = new DataPackage();
            package.SetText(report);
            Clipboard.SetContent(package);
            Clipboard.Flush();
            DiagButton.Content = Loc.Get("DiagCopied");
            await Task.Delay(2000);
        }
        catch (Exception ex)
        {
            AppLog.Error("No se pudo copiar el diagnóstico", ex);
        }
        finally
        {
            DiagButton.Content = Loc.Get("DiagButton");
            DiagButton.IsEnabled = true;
        }
    }

    private void LogsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AppInfo.LogDirectory);
            // Ruta fija de la propia app: se abre en el Explorador.
            Process.Start(new ProcessStartInfo { FileName = AppInfo.LogDirectory, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Error("No se pudo abrir la carpeta de registros", ex);
        }
    }
}
