using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Roshiq.Core.Localization;
using Roshiq.Recommendations;

namespace Roshiq.App.Views;

/// <summary>Помощник: таймеры событий матча, отметка Рошана и ситуативные подсказки по инвентарю.</summary>
public partial class MatchToolsView : UserControl, IRefreshable
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _ready;

    public MatchToolsView()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => UpdateLive();
        Loaded += (_, _) => { _timer.Start(); UpdateLive(); };
        Unloaded += (_, _) => _timer.Stop();
        Loc.Instance.PropertyChanged += (_, _) => { if (IsLoaded) Refresh(); };
    }

    public void Refresh()
    {
        _ready = false;
        var s = App.Host.Settings.Current;
        TimersEnabled.IsChecked = s.Timers.Enabled;
        TimerToasts.IsChecked = s.Timers.Toasts;
        AlertsEnabled.IsChecked = s.Alerts.Enabled;
        AlertTp.IsChecked = s.Alerts.NoTeleport;
        AlertStash.IsChecked = s.Alerts.Stash;
        AlertGold.IsChecked = s.Alerts.UnspentGold;
        GoldThreshold.Text = s.Alerts.UnspentGoldThreshold.ToString();
        AlertBuyback.IsChecked = s.Alerts.Buyback;
        BuybackMinute.Text = s.Alerts.BuybackFromMinute.ToString();
        AlertPace.IsChecked = s.Alerts.Pace;
        AlertDeathCard.IsChecked = s.Alerts.DeathCard;
        AlertAdaptive.IsChecked = s.Alerts.Adaptive;
        AlertsStatus.Text = "";
        var reduced = MatchAlerts.AdaptiveMultipliers(App.Host.History.All());
        AdaptiveInfo.Text = reduced.Count == 0 ? Loc.T("alert_adaptive_none")
            : Loc.Instance.Format("alert_adaptive_list", string.Join(", ", reduced.Select(x => Loc.T(x.Key + "_name") + " ×" + x.Value.ToString("0"))));
        RoshanHint.Text = Loc.Instance.Format("rosh_hint", s.Overlay.RoshanHotkey);

        TimerKinds.Children.Clear();
        foreach (var rule in App.Host.Database.Timers)
        {
            // чип: название и как часто — «Лотос · каждые 3:00»
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(new TextBlock { Text = rule.Name(Loc.Instance.Language), VerticalAlignment = VerticalAlignment.Center });
            if (rule.IntervalSec > 0)
                content.Children.Add(new TextBlock
                {
                    Text = " · " + Loc.Instance.Format("timer_every", $"{rule.IntervalSec / 60}:{rule.IntervalSec % 60:00}"),
                    Foreground = Ui.Res("Muted"), FontWeight = FontWeights.Normal, VerticalAlignment = VerticalAlignment.Center
                });
            var box = new CheckBox
            {
                Style = (Style)FindResource("Chip"),
                Content = content,
                IsChecked = !s.Timers.Disabled.Contains(rule.Key, StringComparer.OrdinalIgnoreCase),
                Tag = rule.Key,
                ToolTip = Loc.Instance.Format("timer_chip_tip", $"{rule.FirstSec / 60}:{rule.FirstSec % 60:00}")
            };
            box.Checked += TimerKind_Changed;
            box.Unchecked += TimerKind_Changed;
            TimerKinds.Children.Add(box);
        }
        _ready = true;
        UpdateLive();
    }

    private void Timers_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        App.Host.Settings.Update(s =>
        {
            s.Timers.Enabled = TimersEnabled.IsChecked == true;
            s.Timers.Toasts = TimerToasts.IsChecked == true;
        });
        UpdateLive();
    }

    private void TimerKind_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready || sender is not CheckBox { Tag: string key } box) return;
        App.Host.Settings.Update(s =>
        {
            s.Timers.Disabled.RemoveAll(x => string.Equals(x, key, StringComparison.OrdinalIgnoreCase));
            if (box.IsChecked != true) s.Timers.Disabled.Add(key);
        });
        UpdateLive();
    }

    private void Alerts_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        App.Host.Settings.Update(s =>
        {
            s.Alerts.Enabled = AlertsEnabled.IsChecked == true;
            s.Alerts.NoTeleport = AlertTp.IsChecked == true;
            s.Alerts.Stash = AlertStash.IsChecked == true;
            s.Alerts.UnspentGold = AlertGold.IsChecked == true;
            if (int.TryParse(GoldThreshold.Text.Trim(), out var g)) s.Alerts.UnspentGoldThreshold = g;
            s.Alerts.Buyback = AlertBuyback.IsChecked == true;
            if (int.TryParse(BuybackMinute.Text.Trim(), out var bm)) s.Alerts.BuybackFromMinute = bm;
            s.Alerts.Pace = AlertPace.IsChecked == true;
            s.Alerts.DeathCard = AlertDeathCard.IsChecked == true;
            s.Alerts.Adaptive = AlertAdaptive.IsChecked == true;
        });
        _ready = false;
        GoldThreshold.Text = App.Host.Settings.Current.Alerts.UnspentGoldThreshold.ToString();
        BuybackMinute.Text = App.Host.Settings.Current.Alerts.BuybackFromMinute.ToString();
        _ready = true;
        AlertsStatus.Text = Loc.T("saved");
    }

    private void RoshanKill_Click(object sender, RoutedEventArgs e)
    {
        App.Host.ToggleRoshanMark();
        UpdateLive();
    }

    private void RoshanClear_Click(object sender, RoutedEventArgs e)
    {
        App.Host.ClearRoshanMark();
        UpdateLive();
    }

    /// <summary>Ближайшие события и состояние Рошана (раз в секунду).</summary>
    private void UpdateLive()
    {
        var L = Loc.Instance;
        var host = App.Host;
        UpcomingPanel.Children.Clear();
        var clock = host.GameClock;
        if (clock is not int c)
        {
            UpcomingPanel.Children.Add(Ui.State(UiState.Disconnected, L.Get("timers_no_clock_title"), L.Get("timers_no_clock")));
            RoshanStatusText.Text = "—";
            return;
        }
        var t = host.Settings.Current.Timers;
        int i = 1;
        foreach (var ev in GameTimers.Upcoming(host.Database.Timers, c, host.TimerOn, 5))
            UpcomingPanel.Children.Add(Ui.Numbered(i++, ev.Rule.Name(L.Language),
                L.Format("timers_at", GameTimers.Clock(ev.AtSec)), GameTimers.Clock(ev.InSec)));
        if (i == 1) UpcomingPanel.Children.Add(Ui.State(UiState.Empty, L.Get("timers_none"), ""));

        var r = host.Roshan.State(c);
        RoshanStatusText.Text = r.Phase switch
        {
            RoshanPhase.Dead => L.Format("rosh_state_dead", GameTimers.Clock(r.RespawnMinAt), GameTimers.Clock(r.RespawnMaxAt))
                                + (r.AegisLeftSec > 0 ? " · " + L.Format("rosh_state_aegis", GameTimers.Clock(r.AegisLeftSec)) : ""),
            RoshanPhase.MaybeAlive => L.Format("rosh_state_maybe", GameTimers.Clock(r.RespawnMaxAt)),
            _ => L.Get("rosh_state_none")
        };
    }
}
