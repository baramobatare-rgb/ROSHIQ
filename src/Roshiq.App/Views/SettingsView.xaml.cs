using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Roshiq.App.Services;
using Roshiq.Core.Localization;
using Roshiq.Core.Models;
using Roshiq.Core.Settings;
using Roshiq.GSI;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Roshiq.Data;
using Microsoft.Win32;
using Lang = Roshiq.Core.Models.Language;

namespace Roshiq.App.Views;

public partial class SettingsView : UserControl, IRefreshable
{
    private bool _ready;

    public SettingsView()
    {
        InitializeComponent();
        LanguageBox.Items.Add(new Choice<Lang>(Lang.Russian, "Русский"));
        LanguageBox.Items.Add(new Choice<Lang>(Lang.English, "English"));
        App.Host.SteamChanged += (_, _) => Dispatcher.InvokeAsync(() => { if (IsLoaded) RefreshSteam(); });
        VolumeSlider.PreviewMouseLeftButtonUp += (_, _) => VolumePreview();
    }

    public void Refresh()
    {
        _ready = false;
        var s = App.Host.Settings.Current;
        BuildThemePanel(s.Theme);
        BuildBackdropPanel(s.Background);
        ReduceMotionBox.IsChecked = s.ReduceMotion;
        CursorGlowBox.IsChecked = s.CursorGlow;
        UiScaleSlider.Value = s.UiScale;
        UiScaleText.Text = s.UiScale.ToString("P0");
        Ui.SelectValue(LanguageBox, s.Language);
        AutostartBox.IsChecked = Autostart.IsEnabled();
        TrayMinimizeBox.IsChecked = s.MinimizeToTray;
        TrayCloseBox.IsChecked = s.CloseToTray;
        PersonalStatsBox.IsChecked = s.Data.UsePersonalStats;
        (s.Preset switch { HintPreset.Minimal => PresetMin, HintPreset.Full => PresetFull, _ => PresetNormal }).IsChecked = true;
        PresetDescription.Text = Loc.T("preset_desc_" + s.Preset.ToString().ToLowerInvariant());
        AdvancedBox.IsChecked = s.ShowAdvancedSettings;
        ApplyAdvancedVisibility(s.ShowAdvancedSettings);
        RefreshSteam();
        var a = s.Automation;
        AutoWatchBox.IsChecked = a.WatchGame;
        AutoHideBox.IsChecked = a.HideWindowOnGameStart;
        AutoShowBox.IsChecked = a.ShowWindowOnGameExit;
        AutoHideBox.IsEnabled = AutoShowBox.IsEnabled = a.WatchGame;
        AutoRoleBox.IsChecked = a.AutoRole;
        AutoModesBox.IsChecked = a.AutoOverlayModes;
        AutoImportBox.IsChecked = a.AutoImport;
        AutoReviewBox.IsChecked = a.AutoReview;
        LaneTipsBox.IsChecked = a.LaneTips;
        PowerSpikesBox.IsChecked = a.PowerSpikes;
        ShowBuildBox.IsChecked = a.ShowBuild;
        DraftToastsBox.IsChecked = a.DraftCounterToasts;
        PostMatchBox.IsChecked = a.PostMatchSummary;
        RefreshSounds();
        CtxReference.IsChecked = s.UsageContext == UsageContext.Reference;
        CtxTraining.IsChecked = s.UsageContext == UsageContext.Training;

        GsiEnabled.IsChecked = s.Gsi.Enabled;
        GsiPort.Text = s.Gsi.Port.ToString();
        if (string.IsNullOrWhiteSpace(DotaPath.Text)) DotaPath.Text = SteamLocator.FindDotaRoot() ?? "";
        UpdateGsiStatus();

        var v = s.Vision;
        VisionEnabled.IsChecked = v.AutoDraftEnabled;
        VisionInterval.Text = v.IntervalMs.ToString();
        VisionThreshold.Text = v.MatchThreshold.ToString("0.###", CultureInfo.InvariantCulture);
        LRadiant.Text = F(v.Layout.RadiantStartX);
        LDire.Text = F(v.Layout.DireStartX);
        LSlotW.Text = F(v.Layout.SlotWidth);
        LSlotH.Text = F(v.Layout.SlotHeight);
        LStep.Text = F(v.Layout.SlotStep);
        LTop.Text = F(v.Layout.Top);
        VisionStatus.Text = Loc.Instance.Format("set_vision_templates", App.Host.Vision.Matcher.TemplateCount, App.Host.Vision.Matcher.HeroCount);

        StoragePath.Text = AppPaths.DataRoot;
        _ready = true;
    }

    private void BuildThemePanel(string current)
    {
        ThemePanel.Children.Clear();
        foreach (var p in ThemeManager.Palettes)
        {
            // превью темы — её иконка; если не загрузилась, остаётся цветная точка
            var logo = ThemeManager.LogoFor(p);
            FrameworkElement swatch = logo != null
                ? new Image
                {
                    Source = logo, Width = 20, Height = 20, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center
                }
                : new System.Windows.Shapes.Ellipse
                {
                    Width = 10, Height = 10, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center,
                    Fill = new System.Windows.Media.SolidColorBrush(p.Accent)
                };
            System.Windows.Media.RenderOptions.SetBitmapScalingMode(swatch, System.Windows.Media.BitmapScalingMode.HighQuality);
            var label = new TextBlock { Text = Loc.Instance.Language == Lang.Russian ? p.NameRu : p.NameEn, VerticalAlignment = VerticalAlignment.Center };
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(swatch);
            content.Children.Add(label);
            var rb = new RadioButton
            {
                Style = (Style)FindResource("Segment"),
                GroupName = "Theme",
                Content = content,
                Tag = p.Key,
                Margin = new Thickness(0, 0, 8, 8),
                IsChecked = p.Key == current
            };
            rb.Checked += (_, _) =>
            {
                if (!_ready) return;
                ThemeManager.Apply(p.Key);
                App.Host.Settings.Update(x => x.Theme = p.Key);
            };
            ThemePanel.Children.Add(rb);
        }
    }

    private void BuildBackdropPanel(string current)
    {
        BackdropPanel.Children.Clear();
        foreach (var b in ThemeManager.Backdrops)
        {
            // превью фона — квадрат его цвета с рамкой панели
            var swatch = new Border
            {
                Width = 16, Height = 16, CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center,
                Background = new System.Windows.Media.SolidColorBrush(b.Bg),
                BorderBrush = new System.Windows.Media.SolidColorBrush(b.LineStrong), BorderThickness = new Thickness(1)
            };
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(swatch);
            content.Children.Add(new TextBlock { Text = Loc.Instance.Language == Lang.Russian ? b.NameRu : b.NameEn, VerticalAlignment = VerticalAlignment.Center });
            var rb = new RadioButton
            {
                Style = (Style)FindResource("Segment"),
                GroupName = "Backdrop",
                Content = content,
                Tag = b.Key,
                Margin = new Thickness(0, 0, 8, 8),
                IsChecked = b.Key == (ThemeManager.Backdrops.Any(x => x.Key == current) ? current : ThemeManager.Backdrops[0].Key)
            };
            rb.Checked += (_, _) =>
            {
                if (!_ready) return;
                ThemeManager.ApplyBackdrop(b.Key);
                App.Host.Settings.Update(x => x.Background = b.Key);
            };
            BackdropPanel.Children.Add(rb);
        }
    }

    private void UiScale_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready) return;
        var v = Math.Round(UiScaleSlider.Value, 2);
        UiScaleText.Text = v.ToString("P0");
        App.Host.Settings.Update(x => x.UiScale = v);
    }

    private void Motion_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        App.Host.Settings.Update(s => s.ReduceMotion = ReduceMotionBox.IsChecked == true);
        if (Application.Current.MainWindow is FrameworkElement root) Motion.ResetTree(root);
    }

    private static string F(double d) => d.ToString("0.####", CultureInfo.InvariantCulture);

    private static double D(TextBox box, double fallback) =>
        double.TryParse(box.Text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : fallback;

    private void UpdateGsiStatus()
    {
        var root = DotaPath.Text.Trim();
        GsiStatus.Text = root.Length == 0
            ? Loc.T("set_dota_not_found")
            : GsiConfigWriter.Exists(root)
                ? Loc.Instance.Format("set_cfg_exists", GsiConfigWriter.TargetPath(root))
                : Loc.Instance.Format("set_cfg_missing", GsiConfigWriter.TargetPath(root));
    }

    private void Language_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || !Ui.TryValue<Lang>(LanguageBox, out var lang)) return;
        App.Host.Settings.Update(s => s.Language = lang);
    }

    private void Glow_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        App.Host.Settings.Update(s => s.CursorGlow = CursorGlowBox.IsChecked == true);
    }

    private void Tray_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        App.Host.Settings.Update(s =>
        {
            s.MinimizeToTray = TrayMinimizeBox.IsChecked == true;
            s.CloseToTray = TrayCloseBox.IsChecked == true;
        });
    }

    // ------------------------------------------------------------------ Steam

    private void RefreshSteam()
    {
        var L = Loc.Instance;
        var d = App.Host.Settings.Current.Data;
        long? account = App.Host.SteamAccount;
        var linked = account != null ? Visibility.Visible : Visibility.Collapsed;
        SteamUnlinkButton.Visibility = linked;
        SteamRefreshButton.Visibility = linked;
        SteamSignInButton.Content = L.Get(account != null ? "steam_relink" : "steam_signin").ToUpperInvariant();
        if (account == null)
        {
            SteamNameText.Text = L.Get("steam_not_linked");
            SteamDetails.Text = L.Get("steam_not_linked_hint");
            SteamWarning.Visibility = Visibility.Collapsed;
            SetAvatar(null);
            return;
        }
        SteamNameText.Text = d.SteamName ?? L.Format("steam_account", account);
        var parts = new List<string> { "ID " + account };
        if (Ranks.Name(d.RankTier, d.LeaderboardRank, L.Language) is string rank) parts.Add(rank);
        parts.Add(L.Get("steam_via_" + (d.SteamLinkedVia ?? "manual")));
        if (App.Host.PlayerHeroes.Count > 0) parts.Add(L.Format("steam_heroes_count", App.Host.PlayerHeroes.Count));
        SteamDetails.Text = string.Join(" · ", parts);
        string? warn = !d.AllowOnlineStats ? L.Get("steam_need_network") : d.PublicMatchData == false ? L.Get("steam_private") : null;
        SteamWarning.Text = warn ?? "";
        SteamWarning.Visibility = warn != null ? Visibility.Visible : Visibility.Collapsed;
        SetAvatar(d.AllowOnlineStats ? d.SteamAvatarUrl : null);
    }

    private void SetAvatar(string? url)
    {
        ImageSource? img = null;
        if (url != null && url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var b = new BitmapImage();
                b.BeginInit();
                b.UriSource = new Uri(url);
                b.DecodePixelWidth = 104;
                b.CacheOption = BitmapCacheOption.OnLoad;
                b.EndInit();
                img = b;
            }
            catch { img = null; }
        }
        SteamAvatar.Background = img != null ? new ImageBrush(img) { Stretch = Stretch.UniformToFill } : (Brush)FindResource("Panel3");
        SteamAvatarIcon.Visibility = img != null ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void SteamSignIn_Click(object sender, RoutedEventArgs e)
    {
        SteamSignInButton.IsEnabled = false;
        SteamStatus.Text = Loc.T("steam_signin_wait");
        try
        {
            var id = await SteamLink.SignInAsync(CancellationToken.None);
            if (id == null) { SteamStatus.Text = Loc.T("steam_signin_failed"); return; }
            App.Host.LinkSteam(id, "openid");
            SteamStatus.Text = Loc.T("steam_linked");
        }
        finally { SteamSignInButton.IsEnabled = true; }
    }

    private void SteamFindLocal_Click(object sender, RoutedEventArgs e)
    {
        var L = Loc.Instance;
        SteamLocalList.Children.Clear();
        var users = SteamLink.LocalUsers();
        if (users.Count == 0) { SteamStatus.Text = L.Get("steam_local_none"); return; }
        SteamStatus.Text = L.Get("steam_local_pick");
        foreach (var u in users.Take(6))
        {
            var button = new Button
            {
                Style = (Style)FindResource("GhostButton"),
                Content = L.Format("steam_link_user", u.Display) + (u.MostRecent ? " · " + L.Get("steam_last_login") : ""),
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 4, 0, 0)
            };
            button.Click += (_, _) =>
            {
                App.Host.LinkSteam(u.SteamId64, "local", u.PersonaName);
                SteamLocalList.Children.Clear();
                SteamStatus.Text = L.Get("steam_linked");
            };
            SteamLocalList.Children.Add(button);
        }
    }

    private async void SteamRefresh_Click(object sender, RoutedEventArgs e)
    {
        SteamRefreshButton.IsEnabled = false;
        SteamStatus.Text = Loc.T("steam_refreshing");
        try { SteamStatus.Text = await App.Host.RefreshSteamProfileAsync() ?? Loc.T("steam_refreshed"); }
        finally { SteamRefreshButton.IsEnabled = true; }
    }

    private void SteamUnlink_Click(object sender, RoutedEventArgs e)
    {
        App.Host.UnlinkSteam();
        SteamLocalList.Children.Clear();
        SteamStatus.Text = Loc.T("steam_unlinked");
    }

    private void PersonalStats_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        App.Host.Settings.Update(s => s.Data.UsePersonalStats = PersonalStatsBox.IsChecked == true);
    }

    // ------------------------------------------------------------------ режим подсказок и расширенные настройки

    private void Preset_Checked(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        var preset = PresetMin.IsChecked == true ? HintPreset.Minimal : PresetFull.IsChecked == true ? HintPreset.Full : HintPreset.Normal;
        App.Host.Settings.Update(s => HintPresets.Apply(s, preset));
        Refresh();
    }

    private void Advanced_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        bool on = AdvancedBox.IsChecked == true;
        App.Host.Settings.Update(s => s.ShowAdvancedSettings = on);
        ApplyAdvancedVisibility(on);
    }

    private void ApplyAdvancedVisibility(bool on)
    {
        var v = on ? Visibility.Visible : Visibility.Collapsed;
        VisionCard.Visibility = v;
        StorageCard.Visibility = v;
        GsiAdvanced.Visibility = v;
    }

    private void Diagnostics_Click(object sender, RoutedEventArgs e) => DiagnosticsWindow.Open();

    // ------------------------------------------------------------------ звуки

    private bool _soundsReady;

    private void RefreshSounds()
    {
        _soundsReady = false;
        var snd = App.Host.Settings.Current.Sounds;
        SoundsBox.IsChecked = snd.Enabled;
        VolumeSlider.Value = snd.Volume;
        VolumeText.Text = snd.Volume.ToString("P0");
        SoundsGrid.IsEnabled = snd.Enabled;
        SoundsGrid.Opacity = snd.Enabled ? 1 : 0.5;
        BuildSoundChoices(ReminderSounds, snd.Reminder);
        BuildSoundChoices(WarningSounds, snd.Warning);
        BuildSoundChoices(EventSounds, snd.Event);
        _soundsReady = true;
    }

    /// <summary>Кнопки выбора звука; выбор сразу проигрывается.</summary>
    private void BuildSoundChoices(WrapPanel panel, string current)
    {
        panel.Children.Clear();
        var kind = (string)panel.Tag;
        foreach (var name in Roshiq.Core.Settings.SoundSettings.Names)
        {
            var rb = new RadioButton
            {
                Style = (Style)FindResource("Segment"), GroupName = "snd_" + kind, Margin = new Thickness(0, 0, 6, 6),
                Content = Loc.T("snd_" + name), IsChecked = name == current
            };
            var chosen = name;
            rb.Checked += (_, _) =>
            {
                if (!_soundsReady) return;
                App.Host.Settings.Update(s =>
                {
                    if (kind == "Reminder") s.Sounds.Reminder = chosen;
                    else if (kind == "Warning") s.Sounds.Warning = chosen;
                    else s.Sounds.Event = chosen;
                });
                App.Host.Sound.Preview(chosen, App.Host.Settings.Current.Sounds.Volume);
            };
            panel.Children.Add(rb);
        }
    }

    private void Sounds_Changed(object sender, RoutedEventArgs e)
    {
        if (!_soundsReady) return;
        bool on = SoundsBox.IsChecked == true;
        App.Host.Settings.Update(s => s.Sounds.Enabled = on);
        RefreshSounds();
        if (on) App.Host.Sound.Preview(App.Host.Settings.Current.Sounds.Warning, App.Host.Settings.Current.Sounds.Volume);
    }

    private void Volume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_soundsReady) return;
        double v = Math.Round(VolumeSlider.Value, 2);
        VolumeText.Text = v.ToString("P0");
        App.Host.Settings.Update(s => s.Sounds.Volume = v);
    }

    /// <summary>Проиграть звук предупреждения при отпускании ползунка громкости.</summary>
    private void VolumePreview() => App.Host.Sound.Preview(App.Host.Settings.Current.Sounds.Warning, App.Host.Settings.Current.Sounds.Volume);

    private void RunWizard_Click(object sender, RoutedEventArgs e)
    {
        new FirstRunWindow { Owner = Window.GetWindow(this) }.ShowDialog();
        Refresh();
    }

    private void Automation_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        App.Host.Settings.Update(s =>
        {
            var a = s.Automation;
            a.WatchGame = AutoWatchBox.IsChecked == true;
            a.HideWindowOnGameStart = AutoHideBox.IsChecked == true;
            a.ShowWindowOnGameExit = AutoShowBox.IsChecked == true;
            a.AutoRole = AutoRoleBox.IsChecked == true;
            a.AutoOverlayModes = AutoModesBox.IsChecked == true;
            a.AutoImport = AutoImportBox.IsChecked == true;
            a.AutoReview = AutoReviewBox.IsChecked == true;
            a.LaneTips = LaneTipsBox.IsChecked == true;
            a.PowerSpikes = PowerSpikesBox.IsChecked == true;
            a.ShowBuild = ShowBuildBox.IsChecked == true;
            a.DraftCounterToasts = DraftToastsBox.IsChecked == true;
            a.PostMatchSummary = PostMatchBox.IsChecked == true;
        });
        AutoHideBox.IsEnabled = AutoShowBox.IsEnabled = AutoWatchBox.IsChecked == true;
    }

    private void Autostart_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        bool on = AutostartBox.IsChecked == true;
        if (Autostart.Set(on)) App.Host.Settings.Update(s => s.Autostart = on);
    }

    private void Context_Checked(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        var ctx = CtxTraining.IsChecked == true ? UsageContext.Training : UsageContext.Reference;
        if (ctx == UsageContext.Training)
        {
            var answer = MessageBox.Show(Loc.T("ctx_training_confirm"), "ROSHIQ", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes)
            {
                _ready = false;
                CtxReference.IsChecked = true;
                _ready = true;
                return;
            }
        }
        App.Host.Settings.Update(s => s.UsageContext = ctx);
    }

    private void Detect_Click(object sender, RoutedEventArgs e)
    {
        DotaPath.Text = SteamLocator.FindDotaRoot() ?? "";
        UpdateGsiStatus();
    }

    private void SaveGsi_Click(object sender, RoutedEventArgs e)
    {
        App.Host.Settings.Update(s =>
        {
            s.Gsi.Enabled = GsiEnabled.IsChecked == true;
            if (int.TryParse(GsiPort.Text.Trim(), out var port)) s.Gsi.Port = port;
        });
        Refresh();
        GsiStatus.Text += "  ·  " + Loc.T("saved");
    }

    private void WriteCfg_Click(object sender, RoutedEventArgs e)
    {
        var root = DotaPath.Text.Trim();
        if (root.Length == 0 || !Directory.Exists(Path.Combine(root, "game", "dota")))
        {
            GsiStatus.Text = Loc.T("set_dota_not_found");
            return;
        }
        var g = App.Host.Settings.Current.Gsi;
        var target = GsiConfigWriter.TargetPath(root);
        var answer = MessageBox.Show(Loc.Instance.Format("set_cfg_confirm", target), "ROSHIQ", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;
        try
        {
            GsiConfigWriter.Write(root, g.Port, g.Token);
            UpdateGsiStatus();
        }
        catch (Exception ex)
        {
            GsiStatus.Text = ex.Message;
        }
    }

    private void CopyCfg_Click(object sender, RoutedEventArgs e)
    {
        var g = App.Host.Settings.Current.Gsi;
        try
        {
            Clipboard.SetText(GsiConfigWriter.BuildContent(g.Port, g.Token));
            GsiStatus.Text = Loc.T("set_cfg_copied");
        }
        catch (Exception ex) { GsiStatus.Text = ex.Message; }
    }

    private void NewToken_Click(object sender, RoutedEventArgs e)
    {
        App.Host.Settings.Update(s => s.Gsi.Token = Guid.NewGuid().ToString("N"));
        GsiStatus.Text = Loc.T("set_token_changed");
    }

    private void SaveVision_Click(object sender, RoutedEventArgs e)
    {
        App.Host.Settings.Update(s =>
        {
            var v = s.Vision;
            v.AutoDraftEnabled = VisionEnabled.IsChecked == true;
            if (int.TryParse(VisionInterval.Text.Trim(), out var ms)) v.IntervalMs = ms;
            v.MatchThreshold = D(VisionThreshold, v.MatchThreshold);
            v.Layout.RadiantStartX = Math.Clamp(D(LRadiant, v.Layout.RadiantStartX), 0, 1);
            v.Layout.DireStartX = Math.Clamp(D(LDire, v.Layout.DireStartX), 0, 1);
            v.Layout.SlotWidth = Math.Clamp(D(LSlotW, v.Layout.SlotWidth), 0.005, 0.2);
            v.Layout.SlotHeight = Math.Clamp(D(LSlotH, v.Layout.SlotHeight), 0.005, 0.2);
            v.Layout.SlotStep = Math.Clamp(D(LStep, v.Layout.SlotStep), 0.005, 0.2);
            v.Layout.Top = Math.Clamp(D(LTop, v.Layout.Top), 0, 0.2);
        });
        Refresh();
        VisionStatus.Text += "  ·  " + Loc.T("saved");
    }

    private void OpenTemplates_Click(object sender, RoutedEventArgs e) => OpenFolder(AppPaths.Templates);

    private void OpenFolder_Click(object sender, RoutedEventArgs e) => OpenFolder(AppPaths.DataRoot);

    private void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { StorageStatus.Text = ex.Message; }
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog { FileName = "roshiq-settings.json", Filter = "JSON|*.json" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            App.Host.Settings.Export(dlg.FileName);
            StorageStatus.Text = Loc.T("set_exported");
        }
        catch (Exception ex) { StorageStatus.Text = ex.Message; }
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "JSON|*.json" };
        if (dlg.ShowDialog() != true) return;
        if (App.Host.Settings.Import(dlg.FileName, out var error))
        {
            StorageStatus.Text = Loc.T("set_imported");
            Refresh();
        }
        else
        {
            StorageStatus.Text = Loc.Instance.Format("set_import_failed", error ?? "?");
        }
    }
}
