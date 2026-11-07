using System.Windows;
using System.Windows.Controls;
using Roshiq.App.Services;
using Roshiq.Core.Localization;
using Roshiq.Core.Models;
using Roshiq.Core.Settings;
using Roshiq.GSI;
using AppLanguage = Roshiq.Core.Models.Language;

namespace Roshiq.App.Views;

/// <summary>
/// Мастер первого запуска: язык и тема → роль → связь с игрой (GSI) → Steam → интернет и режим подсказок.
/// Каждый шаг можно пропустить; всё меняется потом в настройках.
/// </summary>
public partial class FirstRunWindow : Window
{
    private const int Steps = 5;
    private int _step;
    private bool _ready;
    private HintPreset _preset = HintPreset.Normal;

    public FirstRunWindow()
    {
        InitializeComponent();
        var s = App.Host.Settings.Current;
        (s.Language == AppLanguage.English ? LangEn : LangRu).IsChecked = true;
        BuildThemes(s.Theme);
        BuildRoles(s.Position);
        OnlineBox.IsChecked = s.Data.AllowOnlineStats;
        AutostartBox.IsChecked = s.Autostart;
        _preset = s.Preset;
        (_preset switch { HintPreset.Minimal => PresetMin, HintPreset.Full => PresetFull, _ => PresetNormal }).IsChecked = true;
        UpdatePresetText();
        _ready = true;
        ShowStep();
        Loc.Instance.PropertyChanged += (_, _) => { if (IsLoaded) { ShowStep(); UpdatePresetText(); } };
    }

    private void ShowStep()
    {
        var L = Loc.Instance;
        StepLabel.Text = L.Format("fr_step", _step + 1, Steps).ToUpperInvariant();
        StepTitle.Text = L.Get("fr_title_" + _step);
        var panels = new FrameworkElement[] { Step0, Step1, Step2, Step3, Step4 };
        for (int i = 0; i < panels.Length; i++) panels[i].Visibility = i == _step ? Visibility.Visible : Visibility.Collapsed;
        BackButton.Visibility = _step == 0 ? Visibility.Hidden : Visibility.Visible;
        NextButton.Content = L.Get(_step == Steps - 1 ? "fr_finish" : "fr_next").ToUpperInvariant();
        ProgressFill.Width = Math.Max(8, (ActualWidth > 0 ? ActualWidth - 56 : 700) * (_step + 1) / Steps);
        if (_step == 2 && string.IsNullOrEmpty(GsiStatus.Text)) UpdateGsiStatus();
    }

    // ------------------------------------------------------------------ шаг 1: язык и тема

    private void Lang_Checked(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        var lang = LangEn.IsChecked == true ? AppLanguage.English : AppLanguage.Russian;
        App.Host.Settings.Update(s => s.Language = lang);
    }

    private void BuildThemes(string current)
    {
        foreach (var p in ThemeManager.Palettes)
        {
            var rb = new RadioButton
            {
                Style = (Style)FindResource("Segment"), GroupName = "Theme", Tag = p.Key, Margin = new Thickness(0, 0, 8, 8),
                Content = Ui.Up(Loc.Instance.Language == AppLanguage.Russian ? p.NameRu : p.NameEn), IsChecked = p.Key == current
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

    // ------------------------------------------------------------------ шаг 2: роль

    private void BuildRoles(Position current)
    {
        foreach (var pos in Enum.GetValues<Position>())
        {
            var rb = new RadioButton
            {
                Style = (Style)FindResource("Segment"), GroupName = "Role", Margin = new Thickness(0, 0, 8, 8),
                Content = Loc.Instance.PositionName(pos), IsChecked = pos == current
            };
            rb.Checked += (_, _) => { if (_ready) App.Host.Settings.Update(s => s.Position = pos); };
            RolePanel.Children.Add(rb);
        }
    }

    // ------------------------------------------------------------------ шаг 3: связь с игрой

    private void UpdateGsiStatus()
    {
        var root = SteamLocator.FindDotaRoot();
        GsiStatus.Text = root == null ? Loc.T("fr_gsi_no_dota")
            : GsiConfigWriter.Exists(root) ? Loc.T("fr_gsi_ok") : Loc.Instance.Format("fr_gsi_found", root);
        GsiButton.IsEnabled = root != null;
    }

    private void Gsi_Click(object sender, RoutedEventArgs e)
    {
        var root = SteamLocator.FindDotaRoot();
        if (root == null) { UpdateGsiStatus(); return; }
        try
        {
            var g = App.Host.Settings.Current.Gsi;
            GsiConfigWriter.Write(root, g.Port, g.Token);
            if (!g.Enabled) App.Host.Settings.Update(s => s.Gsi.Enabled = true);
            GsiStatus.Text = Loc.T("fr_gsi_ok");
        }
        catch (Exception ex) { GsiStatus.Text = ex.Message; }
    }

    // ------------------------------------------------------------------ шаг 4: Steam

    private void SteamLocal_Click(object sender, RoutedEventArgs e)
    {
        var L = Loc.Instance;
        SteamList.Children.Clear();
        var users = SteamLink.LocalUsers();
        if (users.Count == 0) { SteamStatus.Text = L.Get("steam_local_none"); return; }
        SteamStatus.Text = L.Get("steam_local_pick");
        foreach (var u in users.Take(6))
        {
            var b = new Button
            {
                Style = (Style)FindResource("GhostButton"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0),
                Content = L.Format("steam_link_user", u.Display) + (u.MostRecent ? " · " + L.Get("steam_last_login") : "")
            };
            b.Click += (_, _) =>
            {
                App.Host.LinkSteam(u.SteamId64, "local", u.PersonaName);
                SteamList.Children.Clear();
                SteamStatus.Text = L.Format("fr_steam_done", u.Display);
            };
            SteamList.Children.Add(b);
        }
    }

    private async void SteamSignIn_Click(object sender, RoutedEventArgs e)
    {
        SteamSignIn.IsEnabled = false;
        SteamStatus.Text = Loc.T("steam_signin_wait");
        try
        {
            var id = await SteamLink.SignInAsync(CancellationToken.None);
            if (id == null) { SteamStatus.Text = Loc.T("steam_signin_failed"); return; }
            App.Host.LinkSteam(id, "openid");
            SteamStatus.Text = Loc.T("steam_linked");
        }
        finally { SteamSignIn.IsEnabled = true; }
    }

    // ------------------------------------------------------------------ шаг 5: интернет и режим подсказок

    private void Preset_Checked(object sender, RoutedEventArgs e)
    {
        _preset = PresetMin.IsChecked == true ? HintPreset.Minimal : PresetFull.IsChecked == true ? HintPreset.Full : HintPreset.Normal;
        UpdatePresetText();
    }

    private void UpdatePresetText() =>
        PresetDescription.Text = Loc.T("preset_desc_" + _preset.ToString().ToLowerInvariant());

    // ------------------------------------------------------------------ навигация

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_step > 0) { _step--; ShowStep(); }
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_step < Steps - 1) { _step++; ShowStep(); return; }
        Finish();
    }

    private void Skip_Click(object sender, RoutedEventArgs e) => Finish(skipped: true);

    private void Finish(bool skipped = false)
    {
        bool online = OnlineBox.IsChecked == true;
        bool autostart = AutostartBox.IsChecked == true;
        App.Host.Settings.Update(s =>
        {
            s.SetupVersion = 1;
            if (skipped) return;
            s.Data.AllowOnlineStats = online;
            HintPresets.Apply(s, _preset);
        });
        if (!skipped && autostart != App.Host.Settings.Current.Autostart && Autostart.Set(autostart))
            App.Host.Settings.Update(s => s.Autostart = autostart);
        if (!skipped && online) _ = App.Host.RunOnlineChecksAsync(force: false);
        DialogResult = true;
        Close();
    }
}
