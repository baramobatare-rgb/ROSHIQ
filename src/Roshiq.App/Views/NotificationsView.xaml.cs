using System.Windows;
using System.Windows.Controls;
using Roshiq.Core.Localization;
using Roshiq.Recommendations;

namespace Roshiq.App.Views;

public partial class NotificationsView : UserControl, IRefreshable
{
    public NotificationsView()
    {
        InitializeComponent();
        App.Host.StateChanged += (_, _) => { if (IsLoaded) UpdateTimer(); };
    }

    public void Refresh()
    {
        var n = App.Host.Settings.Current.Notifications;
        Enabled.IsChecked = n.Enabled;
        ObsEnabled.IsChecked = n.ObserverEnabled;
        SentryEnabled.IsChecked = n.SentryEnabled;
        SmokeEnabled.IsChecked = n.SmokeEnabled;
        ConsEnabled.IsChecked = n.ConsumablesEnabled;
        ObsInterval.Text = n.ObserverIntervalSec.ToString();
        SentryInterval.Text = n.SentryIntervalSec.ToString();
        SmokeInterval.Text = n.SmokeIntervalSec.ToString();
        ConsInterval.Text = n.ConsumablesIntervalSec.ToString();
        DisplaySec.Text = n.DisplaySec.ToString();
        GapSec.Text = n.MinGapSec.ToString();
        FirstSec.Text = n.FirstReminderAtSec.ToString();
        Status.Text = "";
        UpdateTimer();
    }

    private static int Parse(TextBox box, int fallback) => int.TryParse(box.Text.Trim(), out var v) ? v : fallback;

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        App.Host.Settings.Update(s =>
        {
            var n = s.Notifications;
            n.Enabled = Enabled.IsChecked == true;
            n.ObserverEnabled = ObsEnabled.IsChecked == true;
            n.SentryEnabled = SentryEnabled.IsChecked == true;
            n.SmokeEnabled = SmokeEnabled.IsChecked == true;
            n.ConsumablesEnabled = ConsEnabled.IsChecked == true;
            n.ObserverIntervalSec = Parse(ObsInterval, n.ObserverIntervalSec);
            n.SentryIntervalSec = Parse(SentryInterval, n.SentryIntervalSec);
            n.SmokeIntervalSec = Parse(SmokeInterval, n.SmokeIntervalSec);
            n.ConsumablesIntervalSec = Parse(ConsInterval, n.ConsumablesIntervalSec);
            n.DisplaySec = Parse(DisplaySec, n.DisplaySec);
            n.MinGapSec = Parse(GapSec, n.MinGapSec);
            n.FirstReminderAtSec = Parse(FirstSec, n.FirstReminderAtSec);
        });
        Refresh(); // показать значения после проверки диапазонов
        Status.Text = Loc.T("saved");
    }

    private void Test_Click(object sender, RoutedEventArgs e)
    {
        var ov = App.Host.Overlay;
        if (!ov.IsVisible)
        {
            Status.Text = Loc.T("notif_test_no_overlay");
            return;
        }
        ov.ShowToast((Loc.T("role_short_" + (int)App.Host.Settings.Current.Position) + " / " + Loc.T("sec_assistant")).ToUpperInvariant(), Loc.T("rem_observer"));
        Status.Text = "";
    }

    private void Timer_Click(object sender, RoutedEventArgs e)
    {
        if (App.Host.ManualTimerRunning) App.Host.StopManualTimer();
        else App.Host.StartManualTimer();
        UpdateTimer();
    }

    private void UpdateTimer()
    {
        var L = Loc.Instance;
        TimerButton.Content = L.Get(App.Host.ManualTimerRunning ? "notif_timer_stop" : "notif_timer_start");
        var r = App.Host.Reminders;
        string Fmt(ReminderKind k) => r.NextDue(k) is TimeSpan t ? $"{(int)t.TotalMinutes}:{t.Seconds:00}" : "—";
        NextPanel.Children.Clear();
        int i = 1;
        foreach (var (kind, key) in new[]
                 {
                     (ReminderKind.ObserverWard, "rem_observer"), (ReminderKind.SentryWard, "rem_sentry"),
                     (ReminderKind.Smoke, "rem_smoke"), (ReminderKind.Consumables, "rem_consumables")
                 })
            NextPanel.Children.Add(Ui.Numbered(i++, L.Get(key), "", Fmt(kind)));
        if (!SupportReminderScheduler.AppliesTo(App.Host.Settings.Current.Position))
            NextPanel.Children.Add(Ui.State(UiState.Empty, L.Get("state_not_support_title"), L.Get("notif_not_support")));
    }
}
