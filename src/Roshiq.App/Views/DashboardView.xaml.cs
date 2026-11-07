using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Roshiq.Core.Diagnostics;
using Roshiq.Core.Localization;
using Roshiq.Core.Models;
using Roshiq.Recommendations;

namespace Roshiq.App.Views;

/// <summary>01 / Обзор: состояние игры, последний герой, быстрый доступ, показатели.</summary>
public partial class DashboardView : UserControl, IRefreshable
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };

    public DashboardView()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => Refresh();
        Loaded += (_, _) => { _timer.Start(); Refresh(); };
        Unloaded += (_, _) => _timer.Stop();
        App.Host.StateChanged += (_, _) => { if (IsLoaded) Refresh(); };
        Loc.Instance.PropertyChanged += (_, _) => { if (IsLoaded) Refresh(); };
    }

    private string? _lastHeadline;
    private string? _lastFocusMode;

    /// <summary>Две-три строки о главном в текущей ситуации.</summary>
    private void ShowFocus(Roshiq.GSI.GameStateSnapshot? gsi)
    {
        var h = App.Host;
        var L = Loc.Instance;
        FocusPanel.Children.Clear();
        void Line(string label, string text, string brush = "Accent")
        {
            var row = Ui.Row(text, "", label, brush);
            row.Margin = new Thickness(0, 2, 0, 4);
            FocusPanel.Children.Add(row);
        }

        string mode;
        if (gsi != null && gsi.IsInProgress)
        {
            mode = "match";
            var timers = h.TimersLineText;
            if (timers.Length > 0) Line(L.Get("focus_timers"), timers);
            var next = h.CurrentBuild?.NextItem(gsi.InventoryItems, gsi.ClockTime);
            if (next != null)
                Line(L.Get("focus_next"), next.Item.DisplayName(L.Language) + (h.GoldLine(next.Item.Key) is string g ? " · " + g : ""));
            if (h.Goals.Active is { } goal) Line(L.Get("focus_goal"), h.GoalText(goal));
        }
        else if (gsi != null && gsi.IsHeroSelection || h.CurrentBriefing != null && gsi != null && gsi.GameState == "DOTA_GAMERULES_STATE_PRE_GAME")
        {
            mode = "draft";
            foreach (var (title, text) in h.BriefingRows(3)) Line(title, text, "Purple");
            if (h.Goals.Active is { } goal) Line(L.Get("focus_goal"), h.GoalText(goal));
        }
        else
        {
            mode = "idle";
            var last = h.History.All().FirstOrDefault();
            if (last != null)
            {
                var hero = h.Database.FindHero(last.HeroId)?.DisplayName(L.Language) ?? "?";
                var result = L.Get(last.Result switch { MatchResult.Win => "hist_win", MatchResult.Loss => "hist_loss", _ => "hist_unknown" });
                Line(L.Get("focus_last"), $"{hero} · {result} · K/D/A {last.Kills}/{last.Deaths}/{last.Assists}",
                    last.Result == MatchResult.Loss ? "Red" : "Accent");
                if (DeathAnalysis.Analyze(last) is { WithLotsOfGold: > 0 } d)
                    Line(L.Get("focus_lesson"), L.Format("focus_lesson_gold", d.WithLotsOfGold, DeathAnalysis.LotsOfGold), "Warning");
            }
        }
        // анимация только при смене ситуации, а не при каждом обновлении раз в 2 секунды
        if (mode != _lastFocusMode)
        {
            _lastFocusMode = mode;
            if (FocusPanel.Children.Count > 0) Motion.Enter(FocusPanel, 0, 6);
        }
    }

    private bool _noticeDiagnostics;

    private void NoticeOpen_Click(object sender, RoutedEventArgs e)
    {
        if (_noticeDiagnostics) DiagnosticsWindow.Open();
        else MainWindow.Navigate("settings");
    }

    private void GoalOpen_Click(object sender, RoutedEventArgs e) => MainWindow.Navigate("history");

    private void NoticeDismiss_Click(object sender, RoutedEventArgs e)
    {
        App.Host.AcknowledgePatch();
        Refresh();
    }

    private string? _avatarUrl;
    private DateTime _weakCheckedUtc;
    private (string Key, double AvgPct, int Matches)? _weak;

    /// <summary>Карточка профиля: ник, медаль и статистика из привязанного Steam либо приглашение привязать.</summary>
    private void RefreshProfile(Roshiq.Core.Settings.AppSettings s, Loc L)
    {
        var d = s.Data;
        if (App.Host.SteamAccount is not long account)
        {
            ProfileName.Text = L.Get("dash_steam_link");
            ProfileSub.Text = L.Get("dash_steam_link_hint");
            ProfileButton.Content = L.Get("dash_steam_link_btn").ToUpperInvariant();
            ProfileWeak.Visibility = Visibility.Collapsed;
            SetProfileAvatar(null);
            return;
        }
        ProfileName.Text = d.SteamName ?? L.Format("steam_account", account);
        var parts = new List<string>();
        if (Roshiq.Data.Ranks.Name(d.RankTier, d.LeaderboardRank, L.Language) is string rank) parts.Add(rank);
        var stats = App.Host.PlayerHeroes;
        if (stats.Count > 0)
        {
            int games = stats.All.Values.Sum(x => x.Games), wins = stats.All.Values.Sum(x => x.Wins);
            parts.Add(L.Format("dash_steam_games", games, games == 0 ? 0 : (double)wins / games));
            var top = stats.All.Values.OrderByDescending(x => x.Games).FirstOrDefault();
            if (top != null && App.Host.Database.FindHero(top.HeroId) is { } hero)
                parts.Add(L.Format("dash_steam_top", hero.DisplayName(L.Language), top.Games));
        }
        if (d.PublicMatchData == false) parts.Add(L.Get("steam_private_short"));
        ProfileSub.Text = parts.Count > 0 ? string.Join(" · ", parts) : L.Get("dash_steam_no_stats");
        ProfileButton.Content = L.Get("dash_steam_open").ToUpperInvariant();
        // что проседает чаще всего по последним разборам матчей
        if ((DateTime.UtcNow - _weakCheckedUtc).TotalSeconds > 30)
        {
            _weakCheckedUtc = DateTime.UtcNow;
            _weak = MatchReviewStore.Weakest(App.Host.Reviews.Recent(10));
        }
        if (_weak is { } w)
        {
            var (ru, en) = MatchReviewBuilder.BenchmarkNames[w.Key];
            ProfileWeak.Text = L.Format("dash_weak", L.Language == Roshiq.Core.Models.Language.English ? en : ru, (int)Math.Round((1 - w.AvgPct) * 100), w.Matches);
            ProfileWeak.Visibility = Visibility.Visible;
        }
        else ProfileWeak.Visibility = Visibility.Collapsed;
        SetProfileAvatar(d.AllowOnlineStats ? d.SteamAvatarUrl : null);
    }

    private void SetProfileAvatar(string? url)
    {
        if (url == _avatarUrl) return;
        _avatarUrl = url;
        System.Windows.Media.ImageSource? img = null;
        if (url != null && url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var b = new System.Windows.Media.Imaging.BitmapImage();
                b.BeginInit();
                b.UriSource = new Uri(url);
                b.DecodePixelWidth = 80;
                b.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                b.EndInit();
                img = b;
            }
            catch { img = null; }
        }
        ProfileAvatar.Background = img != null
            ? new System.Windows.Media.ImageBrush(img) { Stretch = System.Windows.Media.Stretch.UniformToFill }
            : (System.Windows.Media.Brush)FindResource("Panel3");
        ProfileAvatarIcon.Visibility = img != null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ProfileOpen_Click(object sender, RoutedEventArgs e)
    {
        if (Application.Current.MainWindow is MainWindow w) w.Select("settings");
    }

    public void Refresh()
    {
        var h = App.Host;
        var L = Loc.Instance;
        var s = h.Settings.Current;
        var game = h.Overlay.Game;
        var gsi = h.LastGsi;
        bool gsiFresh = gsi != null && DateTime.UtcNow - gsi.ReceivedUtc < TimeSpan.FromSeconds(40);

        RefreshProfile(s, L);

        // --- уведомления о патче и обновлении
        var notices = new List<string>();
        _noticeDiagnostics = h.SafeMode || h.GsiWatch.Current != Roshiq.Core.Diagnostics.GsiProblem.None;
        if (h.SafeMode) notices.Add(L.Get("dash_notice_safe"));
        if (h.GsiWatch.Current != Roshiq.Core.Diagnostics.GsiProblem.None) notices.Add(L.Get("dash_notice_gsi"));
        if (h.NewPatchNotice is string patch) notices.Add(L.Format("dash_notice_patch", patch));
        if (h.AvailableUpdate is { } upd) notices.Add(L.Format("dash_notice_update", upd.Tag));
        NoticeCard.Visibility = notices.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        NoticeText.Text = string.Join("\n", notices);
        NoticeDismiss.Visibility = h.NewPatchNotice != null ? Visibility.Visible : Visibility.Collapsed;

        // --- цель на игру
        var goal = h.Goals.Active;
        GoalCard.Visibility = goal != null ? Visibility.Visible : Visibility.Collapsed;
        if (goal != null)
        {
            GoalLine.Text = h.GoalText(goal);
            GoalSub.Text = L.Format("goal_progress", goal.Achieved, GoalEngine.AchieveToClose, goal.Results.Count, GoalEngine.MaxAttempts);
        }

        // --- главный заголовок: что происходит прямо сейчас
        HeroEyebrow.Inlines.Clear();
        HeroEyebrow.Inlines.Add(new System.Windows.Documents.Run("00 / ") { Foreground = Ui.Res("Accent") });
        HeroEyebrow.Inlines.Add(new System.Windows.Documents.Run(Ui.Up(L.Get("dash_now"))));
        if (gsiFresh && gsi!.IsHeroSelection)
        {
            Headline.Text = Ui.Up(L.Get("dash_head_draft"));
            HeadlineSub.Text = L.Get(s.UsageContext == UsageContext.Training ? "dash_head_draft_sub_training" : "dash_head_draft_sub");
        }
        else if (gsiFresh && gsi!.IsInProgress)
        {
            Headline.Text = Ui.Up(L.Get("dash_head_live"));
            HeadlineSub.Text = L.Get("dash_head_live_sub");
        }
        else if (game.Found)
        {
            Headline.Text = Ui.Up(L.Get("dash_head_ready"));
            HeadlineSub.Text = L.Get(h.Gsi?.LastPacketUtc == null ? "dash_head_ready_nogsi" : "dash_head_ready_sub");
        }
        else
        {
            Headline.Text = Ui.Up(L.Get("dash_head_idle"));
            HeadlineSub.Text = L.Get("dash_head_idle_sub");
        }

        // --- заголовок мягко меняется, когда меняется ситуация
        if (Headline.Text != _lastHeadline)
        {
            if (_lastHeadline != null) Motion.Enter(Headline, 0, 10);
            _lastHeadline = Headline.Text;
        }
        ShowFocus(gsiFresh ? gsi : null);

        // --- портрет текущего героя
        var heroKey = h.CurrentHero?.Key;
        if ((HeroPortraitHost.Content as FrameworkElement)?.Tag as string != heroKey)
        {
            if (h.CurrentHero != null)
            {
                var pic = Ui.Portrait(h.CurrentHero, 200);
                pic.Tag = heroKey;
                HeroPortraitHost.Content = pic;
                Motion.Enter(pic, 0, 8);
            }
            else HeroPortraitHost.Content = null;
        }

        // --- последний герой
        if (h.CurrentHero == null)
        {
            HeroName.Text = "—";
            HeroMeta.Text = L.Get("dash_no_hero");
            NextItem.Text = "—";
            NextItemWhy.Text = "";
            Ui.SetItemImage(NextItemIcon, null);
        }
        else
        {
            HeroName.Text = Ui.Up(h.CurrentHero.DisplayName(L.Language));
            HeroMeta.Text = L.PositionName(s.Position) + (gsi?.HeroLevel is int lvl ? " · " + L.Format("dash_level", lvl) : "");
            var next = h.CurrentBuild?.NextItem(gsi?.InventoryItems, gsi?.ClockTime);
            NextItem.Text = next?.Item.DisplayName(L.Language) ?? L.Get("ov_build_done");
            Ui.SetItemImage(NextItemIcon, next?.Item.Key);
            NextItemWhy.Text = next?.Explain(L.Language) ?? "";
        }
        TestModeButton.Content = Ui.Up(L.Get(h.Overlay.TestMode ? "dash_test_overlay_off" : "dash_test_overlay"));

        // --- плитки
        GameValue.Text = Ui.Up(L.Get(!game.Found ? "tile_off" : game.Minimized ? "tile_minimized" : "tile_on"));
        GameValue.Foreground = Ui.Res(!game.Found ? "Muted" : game.Minimized ? "Warning" : "Accent");
        GameStatus.Text = !game.Found
            ? L.Get("dash_game_not_found")
            : game.Minimized ? L.Get("dash_game_minimized")
            : L.Format("dash_game_found", game.ClientArea.Width, game.ClientArea.Height);

        string gsiKey;
        if (h.Gsi == null) { gsiKey = "tile_off"; GsiStatus.Text = L.Get("dash_gsi_disabled"); }
        else if (!h.Gsi.IsRunning) { gsiKey = "tile_error"; GsiStatus.Text = L.Format("dash_gsi_error", h.Gsi.LastError ?? "?"); }
        else if (h.Gsi.LastPacketUtc == null) { gsiKey = "tile_waiting"; GsiStatus.Text = L.Format("dash_gsi_waiting", h.Gsi.Port); }
        else
        {
            gsiKey = gsiFresh ? "tile_on" : "tile_waiting";
            GsiStatus.Text = L.Format("dash_gsi_ok", h.Gsi.PacketsReceived, h.Gsi.LastPacketUtc.Value.ToLocalTime().ToString("HH:mm:ss"), gsi?.GameState ?? "—");
        }
        GsiValue.Text = Ui.Up(L.Get(gsiKey));
        GsiValue.Foreground = Ui.Res(gsiKey == "tile_on" ? "Accent" : gsiKey == "tile_error" ? "Red" : "Muted");

        OverlayValue.Text = Ui.Up(L.Get(h.Overlay.IsVisible ? "tile_visible" : "tile_hidden"));
        OverlayValue.Foreground = Ui.Res(h.Overlay.IsVisible ? "Accent" : "Muted");
        OverlayStatus.Text = L.Get(h.Overlay.StatusKey) + " · " + L.Get("ovmode_" + s.Overlay.Mode.ToString().ToLowerInvariant())
                             + (h.Overlay.ToggleHotkeyOk ? "" : " · " + L.Format("dash_hotkey_busy", s.Overlay.ToggleHotkey));

        bool training = s.UsageContext == UsageContext.Training;
        ModeValue.Text = Ui.Up(L.Get(training ? "ctx_training_short" : "ctx_reference_short"));
        ModeValue.Foreground = Ui.Res(training ? "Warning" : "Accent");
        ContextHint.Text = L.Get(training ? "ctx_training_hint" : "ctx_reference_hint");

        // --- состояние: ошибка GSI важнее, затем "игра не запущена"
        if (h.Gsi != null && !h.Gsi.IsRunning)
        {
            StateHost.Content = Ui.State(UiState.Error, L.Get("state_gsi_error_title"), L.Format("dash_gsi_error", h.Gsi.LastError ?? "?"));
            StateCard.Visibility = Visibility.Visible;
        }
        else
        {
            StateCard.Visibility = Visibility.Collapsed;
        }

        // «Что делать?» — только когда что-то действительно не так (а не просто «Dota не запущена»)
        bool problem = (h.Gsi != null && !h.Gsi.IsRunning) || h.Gsi == null
                       || h.GsiWatch.Current != Roshiq.Core.Diagnostics.GsiProblem.None
                       || (game.Found && h.Gsi?.LastPacketUtc == null);
        FixButton.Visibility = problem ? Visibility.Visible : Visibility.Collapsed;

        ShowRecent(L);
    }

    private string? _recentKey;

    /// <summary>Последние матчи: полоса побед и поражений, винрейт, KDA и главный урок прошлой игры.</summary>
    private void ShowRecent(Loc L)
    {
        var all = App.Host.History.All();
        var known = all.Where(m => m.Result != MatchResult.Unknown).Take(20).ToList();
        var key = all.Count + "|" + (all.FirstOrDefault()?.Id ?? "") + "|" + L.Language;
        if (key == _recentKey) return;
        _recentKey = key;
        RecentCard.Visibility = all.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (all.Count == 0) return;

        RecentStrip.Children.Clear();
        foreach (var m in all.Take(10).Reverse())
        {
            var hero = App.Host.Database.FindHero(m.HeroId) ?? App.Host.Database.FindHero(m.HeroKey);
            bool win = m.Result == MatchResult.Win, loss = m.Result == MatchResult.Loss;
            var cell = new Border
            {
                Width = 52, Height = 30, CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 0, 6, 6), ClipToBounds = true,
                BorderThickness = new Thickness(0, 0, 0, 3),
                BorderBrush = Ui.Res(win ? "Success" : loss ? "Red" : "Faint"),
                Background = Roshiq.App.Services.HeroArt.Portrait(hero?.Key) is { } img
                    ? new System.Windows.Media.ImageBrush(img) { Stretch = System.Windows.Media.Stretch.UniformToFill }
                    : Ui.Res("Panel3"),
                ToolTip = $"{hero?.DisplayName(L.Language) ?? "?"} · {L.Get(win ? "hist_win" : loss ? "hist_loss" : "hist_unknown")} · {m.Kills}/{m.Deaths}/{m.Assists}"
            };
            RecentStrip.Children.Add(cell);
        }
        if (known.Count > 0)
        {
            int wins = known.Count(m => m.Result == MatchResult.Win);
            double kda = known.Average(m => (m.Kills + m.Assists) / Math.Max(1.0, m.Deaths));
            RecentSummary.Text = L.Format("dash_recent_line", wins * 100 / known.Count, known.Count, kda.ToString("0.0"));
            int streak = 0; var first = known[0].Result;
            foreach (var m in known) { if (m.Result != first) break; streak++; }
            RecentSub.Text = streak >= 2 ? L.Format(first == MatchResult.Win ? "dash_streak_win" : "dash_streak_loss", streak) : "";
        }
        else { RecentSummary.Text = L.Get("dash_recent_noresults"); RecentSub.Text = ""; }

        // главный урок — по последнему записанному матчу (итог матча считается без интернета)
        var last = all.FirstOrDefault(PostMatch.Worth);
        if (last != null && PostMatch.Build(last, all, k => App.Host.Database.FindItem(k)?.Name) is { } sum && (sum.MainMistake ?? sum.Good.FirstOrDefault()) is { } lesson)
        {
            var hero = App.Host.Database.FindHero(last.HeroId);
            LessonText.Text = lesson.Text(L.Language);
            LessonSub.Text = (hero?.DisplayName(L.Language) ?? "?") + " · " + last.StartedUtc.ToLocalTime().ToString("dd.MM HH:mm")
                             + (sum.MainMistake != null ? " · " + L.Get("dash_lesson_fix") : " · " + L.Get("dash_lesson_good"));
        }
        else
        {
            LessonText.Text = L.Get("dash_lesson_none");
            LessonSub.Text = "";
        }
    }

    private void GoHistory_Click(object sender, RoutedEventArgs e) => MainWindow.Navigate("history");

    private void Fix_Click(object sender, RoutedEventArgs e) => DiagnosticsWindow.Open();

    private void GoDraft_Click(object sender, RoutedEventArgs e) => MainWindow.Navigate("draft");

    private void GoBuild_Click(object sender, RoutedEventArgs e) => MainWindow.Navigate("build");

    private void ToggleOverlay_Click(object sender, RoutedEventArgs e)
    {
        App.Host.Overlay.ToggleHidden();
        Refresh();
    }

    private void TestMode_Click(object sender, RoutedEventArgs e)
    {
        App.Host.Overlay.TestMode = !App.Host.Overlay.TestMode;
        Refresh();
    }
}
