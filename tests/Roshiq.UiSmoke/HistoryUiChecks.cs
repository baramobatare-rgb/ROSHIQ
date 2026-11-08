using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Roshiq.App;
using Roshiq.App.Views;
using Roshiq.Core.Localization;
using Roshiq.Core.Models;
using Roshiq.Recommendations;

/// <summary>
/// 0.17.7: регрессия истории в настоящем (но невидимом) окне — список не пересоздаётся без изменений,
/// исправленный итог виден в сводке, фоновое сохранение не сбрасывает выбор и прокрутку,
/// узкое окно ставит разбор под список, у вкладок есть видимый фокус клавиатуры.
/// Очередь диспетчера прокачивается с ограничением по времени: зависание — это провал с понятной меткой, а не вечное ожидание.
/// </summary>
internal static class HistoryUiChecks
{
    private static readonly DateTime Origin = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    internal static void Run(AppHost host)
    {
        var store = host.History;
        for (int i = 0; i < 40; i++)
            store.Save(Game(i, i == 39 ? MatchResult.Unknown : i % 2 == 0 ? MatchResult.Win : MatchResult.Loss));

        var view = new HistoryView();
        var window = new Window
        {
            Content = view, Width = 1160, Height = 900, Left = -20000, Top = -20000,
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, ShowActivated = false
        };
        try
        {
            window.Show();
            Pump("show history window");
            view.Refresh();
            Pump("first history refresh");

            var list = (ListBox)view.FindName("MatchList");
            Require(list.Items.Count == 40, "History list shows every saved game");

            // 1) повторные Refresh без новой версии истории: те же элементы, ни одного разбора JSON
            var firstItem = list.Items[0];
            long reads = store.Deserializations;
            for (int i = 0; i < 5; i++) view.Refresh();
            Pump("idle refreshes");
            Require(ReferenceEquals(list.Items[0], firstItem) && list.Items.Count == 40, "Refresh without a new Revision keeps the same list elements");
            Require(store.Deserializations == reads, "Refresh without a new Revision reads no history");

            // 2) исправление итога у существующего Id — сразу видно и на главной, и в карточке
            var dash = new DashboardView();
            dash.Refresh();
            var summary = (TextBlock)dash.FindName("RecentSummary");
            string before = summary.Text;
            var fix = store.Find("gsi-39")!;
            fix.Result = MatchResult.Win;
            Require(store.Save(fix), "Result fix for an existing Id is saved");
            dash.Refresh();
            Require(summary.Text != before, "Dashboard summary reflects a result fix for an existing Id");
            Pump("refresh after result fix");
            var fixedCard = list.Items.OfType<Choice<MatchRecord>>().Single(c => c.Value.Id == "gsi-39");
            Require(fixedCard.Display.EndsWith(Loc.T("hist_win")), "History card reflects a result fix for an existing Id");

            // 3) фоновые сохранения: выбор и прокрутка на месте, несколько уведомлений — одна перестройка
            list.SelectedIndex = 25;
            list.ScrollIntoView(list.Items[25]);
            Pump("select and scroll");
            var scroll = FindScroll(list);
            double offset = scroll?.VerticalOffset ?? 0;
            string keptId = ((Choice<MatchRecord>)list.SelectedItem).Value.Id;
            Require(scroll != null && offset > 0, "History list is scrolled before a background save");
            var topBeforeSaves = list.Items[0];
            reads = store.Deserializations;
            for (int i = 0; i < 3; i++) store.Save(Game(-1 - i, MatchResult.Loss));   // старее всех: окажутся в конце списка
            Pump("background saves");
            Pump("restore scroll");
            Require(list.Items.Count == 43 && !ReferenceEquals(list.Items[0], topBeforeSaves), "Background save rebuilds the list");
            Require(store.Deserializations - reads <= store.Count + 1, "Several background saves are coalesced into one rebuild");
            Require(((Choice<MatchRecord>)list.SelectedItem).Value.Id == keptId, "Selection survives a background save");
            Require(Math.Abs(FindScroll(list)!.VerticalOffset - offset) < 0.5, "Scroll position survives a background save");

            // 4) узкое окно: разбор под списком, широкое — снова рядом
            var reviewCard = (FrameworkElement)view.FindName("ReviewCard");
            var listCard = (FrameworkElement)view.FindName("ListCard");
            window.Width = 560;
            Pump("narrow layout");
            Require(Grid.GetRow(reviewCard) == 1 && Grid.GetColumn(reviewCard) == 0, "Narrow 560 puts ReviewCard in the second row");
            var listBottom = listCard.TranslatePoint(new Point(0, listCard.ActualHeight), view).Y;
            Require(reviewCard.TranslatePoint(new Point(), view).Y >= listBottom - 0.5, "Narrow 560 shows the report below the list");
            window.Width = 1160;
            Pump("wide layout");
            Require(Grid.GetRow(reviewCard) == 0 && Grid.GetColumn(reviewCard) == 1, "Wide window restores the side-by-side layout");

            // 5) видимый фокус клавиатуры
            var importTab = (Control)view.FindName("TabImport");
            Require(importTab.FocusVisualStyle != null && ReferenceEquals(importTab.FocusVisualStyle, Application.Current.FindResource("KeyboardFocus")),
                "TabImport has the keyboard focus visual");
        }
        finally
        {
            window.Close();
            Pump("close history window");
        }
    }

    private static MatchRecord Game(int i, MatchResult result)
    {
        var m = new MatchRecord
        {
            Id = "gsi-" + i, MatchId = "9" + (i + 100), Source = "GSI", HeroId = 1 + Math.Abs(i) % 100, Position = Position.Mid,
            StartedUtc = Origin.AddHours(i + 10), DurationSec = 2400, Result = result, Ended = true, Kills = 5, Deaths = 3, Assists = 7
        };
        for (int c = 0; c <= 2400; c += 120) m.Samples.Add(new MatchSample { Clock = c, Gold = c, Gpm = 450, Xpm = 500, LastHits = c / 10, Level = 1 + c / 150 });
        m.Coverage.Add(new CoverageSpan { From = 0, To = 2400 });
        return m;
    }

    /// <summary>Прокачать очередь диспетчера до простоя, но не дольше timeoutMs.</summary>
    private static void Pump(string step, int timeoutMs = 5000)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var frame = new DispatcherFrame();
        bool timedOut = false;
        var timer = new DispatcherTimer(DispatcherPriority.Send, dispatcher) { Interval = TimeSpan.FromMilliseconds(timeoutMs) };
        timer.Tick += (_, _) => { timedOut = true; frame.Continue = false; };
        timer.Start();
        dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
        timer.Stop();
        if (timedOut) throw new TimeoutException("Dispatcher did not become idle within " + timeoutMs + " ms: " + step);
    }

    private static ScrollViewer? FindScroll(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer sv) return sv;
            if (FindScroll(child) is { } found) return found;
        }
        return null;
    }

    private static void Require(bool condition, string label)
    {
        if (!condition) throw new Exception(label);
        Console.WriteLine("PASS " + label);
    }
}
