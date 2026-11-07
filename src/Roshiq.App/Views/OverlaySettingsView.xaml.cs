using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Roshiq.Core.Diagnostics;
using Roshiq.Core.Layout;
using Roshiq.Core.Localization;
using Roshiq.Core.Models;
using Roshiq.Overlay;

namespace Roshiq.App.Views;

public partial class OverlaySettingsView : UserControl, IRefreshable
{
    private bool _ready;

    public OverlaySettingsView()
    {
        InitializeComponent();
        Loc.Instance.PropertyChanged += (_, _) => UpdateButtons();
        App.Host.Settings.Changed += (_, _) => { if (IsLoaded) Dispatcher.InvokeAsync(DrawPreview); };
    }

    public void Refresh()
    {
        _ready = false;
        var o = App.Host.Settings.Current.Overlay;
        ModeMinimal.IsChecked = o.Mode == OverlayMode.Minimal;
        ModeExpanded.IsChecked = o.Mode == OverlayMode.Expanded;
        ModeHidden.IsChecked = o.Mode == OverlayMode.Hidden;
        XSlider.Value = o.Panel.X;
        YSlider.Value = o.Panel.Y;
        WSlider.Value = o.Panel.W;
        HSlider.Value = o.Panel.H;
        ScaleSlider.Value = o.Scale;
        OpacitySlider.Value = o.Opacity;
        ToggleHotkey.Text = o.ToggleHotkey;
        DetailsHotkey.Text = o.DetailsHotkey;
        EditHotkey.Text = o.EditHotkey;
        RoshanHotkey.Text = o.RoshanHotkey;
        BuildProfiles();
        HideUnfocused.IsChecked = o.HideWhenGameNotFocused;
        SnapBox.IsChecked = o.SnapToEdges;
        UpdateLayoutsText();
        ProcessName.Text = o.TargetProcess;
        UpdateTexts();
        UpdateButtons();
        DrawPreview();
        _ready = true;
    }

    private void UpdateTexts()
    {
        XText.Text = XSlider.Value.ToString("P0");
        YText.Text = YSlider.Value.ToString("P0");
        WText.Text = WSlider.Value.ToString("P0");
        HText.Text = HSlider.Value.ToString("P0");
        ScaleText.Text = ScaleSlider.Value.ToString("P0");
        OpacityText.Text = OpacitySlider.Value.ToString("P0");
    }

    private void UpdateButtons()
    {
        var ov = App.Host.Overlay;
        EditButton.Content = Ui.Up(Loc.T(ov.EditMode ? "ov_edit_finish" : "ov_edit_start"));
        TestButton.Content = Ui.Up(Loc.T(ov.TestMode ? "dash_test_overlay_off" : "dash_test_overlay"));
    }

    /// <summary>Схема экрана: зоны интерфейса Dota, которые оверлей не перекрывает, и текущее положение панели.</summary>
    private void DrawPreview()
    {
        Preview.Children.Clear();
        const double w = 320, h = 180;
        void AddZone(RelRect r, Brush fill, Brush? stroke, string? label)
        {
            var rect = new Rectangle { Width = r.W * w, Height = r.H * h, Fill = fill, Stroke = stroke, StrokeThickness = stroke == null ? 0 : 1.5, RadiusX = 3, RadiusY = 3 };
            Canvas.SetLeft(rect, r.X * w);
            Canvas.SetTop(rect, r.Y * h);
            Preview.Children.Add(rect);
            if (label == null) return;
            var tb = new TextBlock { Text = label, FontSize = 9.5, Foreground = Ui.Res(stroke == null ? "Faint" : "Accent"), FontFamily = (FontFamily)FindResource("DisplayFont") };
            Canvas.SetLeft(tb, r.X * w + 4);
            Canvas.SetTop(tb, r.Y * h + 2);
            Preview.Children.Add(tb);
        }
        AddZone(SafeZone.Minimap, Ui.Res("Panel3"), null, "MAP");
        AddZone(SafeZone.TopBar, Ui.Res("Panel3"), null, "TOP BAR");
        AddZone(SafeZone.BottomHud, Ui.Res("Panel3"), null, "HUD");
        AddZone(SafeZone.ToastArea, Ui.Res("Panel2"), Ui.Res("LineStrong"), null);

        var o = App.Host.Settings.Current.Overlay;
        if (o.Mode == OverlayMode.Hidden) return;
        var panel = SafeZone.Clamp(o.Panel);
        if (o.Mode == OverlayMode.Minimal) panel = panel with { H = Math.Max(84.0 / 1080, 0.09) };
        panel = panel with { W = Math.Min(1 - panel.X, panel.W * o.Scale), H = Math.Min(1 - panel.Y, panel.H * o.Scale) };
        AddZone(panel, Ui.Res("AccentSoft"), Ui.Res("Accent"), "PULSE");
    }

    private void Mode_Checked(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        var mode = ModeExpanded.IsChecked == true ? OverlayMode.Expanded : ModeHidden.IsChecked == true ? OverlayMode.Hidden : OverlayMode.Minimal;
        App.Host.Settings.Update(s => s.Overlay.Mode = mode);
    }

    private void Geometry_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready) return;
        UpdateTexts();
        var rect = SafeZone.Clamp(new RelRect(XSlider.Value, YSlider.Value, WSlider.Value, HSlider.Value));
        App.Host.Settings.Update(s =>
        {
            s.Overlay.Panel = rect;
            if (App.Host.Overlay.CurrentLayoutKey is string key) s.Overlay.Layouts[key] = rect;
            s.Overlay.Opacity = OpacitySlider.Value;
            s.Overlay.Scale = Math.Round(ScaleSlider.Value, 2);
        });
    }

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        var ov = App.Host.Overlay;
        ov.SetEditMode(!ov.EditMode);
        UpdateButtons();
        if (!ov.EditMode) Refresh(); // показать сохранённое положение
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        App.Host.Settings.Update(s =>
        {
            s.Overlay.Panel = new RelRect(0.78, 0.22, 0.19, 0.30);
            s.Overlay.Scale = 1.0;
        });
        Refresh();
    }

    private void Snap_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        bool on = SnapBox.IsChecked == true;
        App.Host.Settings.Update(s => s.Overlay.SnapToEdges = on);
    }

    private void ResetLayouts_Click(object sender, RoutedEventArgs e)
    {
        App.Host.Overlay.ResetLayouts();
        UpdateLayoutsText();
    }

    /// <summary>Для каких разрешений окна игры запомнено положение панели.</summary>
    private void UpdateLayoutsText()
    {
        var L = Loc.Instance;
        var o = App.Host.Settings.Current.Overlay;
        var current = App.Host.Overlay.CurrentLayoutKey;
        var keys = o.Layouts.Keys.OrderBy(k => k).Select(k => LayoutMemory.Pretty(k) + (k == current ? " ✓" : "")).ToList();
        LayoutsText.Text = keys.Count == 0 ? L.Get("ov_layouts_none") : L.Format("ov_layouts", string.Join(", ", keys));
        ResetLayoutsButton.Visibility = o.Layouts.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Test_Click(object sender, RoutedEventArgs e)
    {
        App.Host.Overlay.TestMode = !App.Host.Overlay.TestMode;
        UpdateButtons();
    }

    private void SaveHotkeys_Click(object sender, RoutedEventArgs e) => SaveHotkeys(
        HotkeyRules.Normalize(ToggleHotkey.Text), HotkeyRules.Normalize(DetailsHotkey.Text),
        HotkeyRules.Normalize(EditHotkey.Text), HotkeyRules.Normalize(RoshanHotkey.Text));

    private void ResetHotkeys_Click(object sender, RoutedEventArgs e)
    {
        var d = new Roshiq.Core.Settings.OverlaySettings();
        ToggleHotkey.Text = d.ToggleHotkey;
        DetailsHotkey.Text = d.DetailsHotkey;
        EditHotkey.Text = d.EditHotkey;
        RoshanHotkey.Text = d.RoshanHotkey;
        SaveHotkeys(d.ToggleHotkey, d.DetailsHotkey, d.EditHotkey, d.RoshanHotkey);
    }

    /// <summary>Проверка (формат, повторы), сохранение и отчёт: что занято другой программой и что пересекается с Dota.</summary>
    private void SaveHotkeys(string toggle, string details, string edit, string roshan)
    {
        var L = Loc.Instance;
        var all = new Dictionary<string, string> { ["toggle"] = toggle, ["details"] = details, ["edit"] = edit, ["roshan"] = roshan };
        if (all.Values.Any(g => !HotkeyManager.TryParse(g, out _, out _)))
        {
            HotkeyStatus.Text = L.Get("ov_hotkey_invalid");
            return;
        }
        var dup = HotkeyRules.Duplicates(all);
        if (dup.Count > 0)
        {
            HotkeyStatus.Text = L.Format("hk_duplicate", string.Join(", ", dup));
            return;
        }
        App.Host.Settings.Update(s =>
        {
            s.Overlay.ToggleHotkey = toggle;
            s.Overlay.DetailsHotkey = details;
            s.Overlay.EditHotkey = edit;
            s.Overlay.RoshanHotkey = roshan;
        });
        // перерегистрация идёт через диспетчер — результат смотрим чуть позже
        Dispatcher.InvokeAsync(() =>
        {
            var notes = new List<string>();
            var busy = App.Host.Overlay.FailedHotkeys;
            if (busy.Count > 0) notes.Add(L.Format("hk_busy", string.Join(", ", busy)));
            var clash = all.Values.Where(HotkeyRules.ClashesWithDota).ToList();
            if (clash.Count > 0) notes.Add(L.Format("hk_dota_clash", string.Join(", ", clash)));
            HotkeyStatus.Text = notes.Count == 0 ? L.Get("hk_saved_ok") : string.Join(" ", notes);
        }, System.Windows.Threading.DispatcherPriority.ContextIdle);
    }

    /// <summary>Кнопки профилей: выбор применяет режим, положение, масштаб и прозрачность.</summary>
    private void BuildProfiles()
    {
        var s = App.Host.Settings.Current;
        ProfilePanel.Children.Clear();
        foreach (var profile in s.OverlayProfiles)
        {
            var rb = new RadioButton
            {
                Style = (Style)FindResource("Segment"),
                GroupName = "OverlayProfile",
                Content = Ui.Up(profile.Name),
                Tag = profile.Id,
                Margin = new Thickness(0, 0, 8, 8),
                IsChecked = profile.Id == s.ActiveOverlayProfile
            };
            rb.Checked += (_, _) =>
            {
                if (!_ready) return;
                var id = profile.Id;
                App.Host.Settings.Update(x =>
                {
                    var p = x.OverlayProfiles.FirstOrDefault(pp => pp.Id == id);
                    if (p == null) return;
                    p.ApplyTo(x.Overlay);
                    x.ActiveOverlayProfile = id;
                });
                var applied = Loc.Instance.Format("prof_applied", profile.Name);
                Dispatcher.InvokeAsync(() => { Refresh(); ProfileStatus.Text = applied; });
            };
            ProfilePanel.Children.Add(rb);
        }
    }

    private void SaveProfile_Click(object sender, RoutedEventArgs e)
    {
        var s = App.Host.Settings.Current;
        var id = s.ActiveOverlayProfile ?? s.OverlayProfiles.FirstOrDefault()?.Id;
        if (id == null) return;
        string name = "";
        App.Host.Settings.Update(x =>
        {
            var p = x.OverlayProfiles.FirstOrDefault(pp => pp.Id == id);
            if (p == null) return;
            p.CopyFrom(x.Overlay);
            x.ActiveOverlayProfile = id;
            name = p.Name;
        });
        ProfileStatus.Text = Loc.Instance.Format("prof_saved", name);
    }

    private void Behavior_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        App.Host.Settings.Update(s =>
        {
            s.Overlay.HideWhenGameNotFocused = HideUnfocused.IsChecked == true;
            if (!string.IsNullOrWhiteSpace(ProcessName.Text)) s.Overlay.TargetProcess = ProcessName.Text.Trim();
        });
    }
}
