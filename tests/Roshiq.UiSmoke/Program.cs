using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Roshiq.App;
using Roshiq.App.Views;
using Roshiq.Core.Models;
using Roshiq.Core.Settings;
using Roshiq.GSI;
using Roshiq.Vision;
using System.IO.Compression;

class Program
{
    static void Check(bool condition, string label) { if (!condition) throw new Exception(label); Console.WriteLine("PASS " + label); }
    static T Control<T>(FrameworkElement parent, string name) where T : class => (T)parent.FindName(name);
    /// <summary>0.18.2: каждый акцент и каждый фон применяются, иконки темы загружаются, фон «Чёрный» — чистый чёрный.</summary>
    static void ThemeChecks()
    {
        var res = Application.Current.Resources;
        System.Windows.Media.Color Bg() => ((System.Windows.Media.SolidColorBrush)res["Bg"]).Color;
        foreach (var p in ThemeManager.Palettes)
        {
            ThemeManager.Apply(p.Key);
            Check(ThemeManager.Current == p.Key && ((System.Windows.Media.SolidColorBrush)res["Accent"]).Color == p.Accent
                  && res["AppLogo"] is System.Windows.Media.ImageSource && ThemeManager.LogoFor(p) != null, "Theme accent and icons: " + p.Key);
        }
        foreach (var b in ThemeManager.Backdrops)
        {
            ThemeManager.ApplyBackdrop(b.Key);
            Check(Bg() == b.Bg && ((System.Windows.Media.SolidColorBrush)res["Panel"]).Color == b.Panel && ThemeManager.CurrentBg == b.Bg, "Background applies: " + b.Key);
        }
        Check(ThemeManager.Backdrops.Single(x => x.Key == "black").Bg == System.Windows.Media.Colors.Black, "Black background is pure black for OLED");
        ThemeManager.Apply("lime", "nonsense");
        Check(ThemeManager.CurrentBackdrop == "standard" && Bg() == ThemeManager.Backdrops[0].Bg, "Unknown background falls back to standard");
    }

    /// <summary>0.18.3: уведомление на большом экране увеличивается вместе с карточкой и не обрезается.</summary>
    static void ToastScaleCheck()
    {
        var toast = new Roshiq.Overlay.ToastWindow { Left = -20000, Top = -20000 };
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(toast);
        var bounds = new Roshiq.Core.Layout.PixelRect(-20000, -20000, 300, 60);
        toast.ShowToast("ROSHIQ", "Нет телепорта — купи ТП", bounds, TimeSpan.FromSeconds(5), 0.9, animate: false, scale: 1.35);
        Check(toast.Width >= 240 * 1.35 - 0.5 && toast.Height >= 68 * 1.35 - 0.5
              && toast.FindName("Root") is FrameworkElement root && root.LayoutTransform is System.Windows.Media.ScaleTransform st && Math.Abs(st.ScaleX - 1.35) < 0.001,
            "Toast grows with the screen scale");
        toast.ShowToast("ROSHIQ", "x", new Roshiq.Core.Layout.PixelRect(-20000, -20000, (int)(400 * dpi.DpiScaleX), (int)(80 * dpi.DpiScaleY)), TimeSpan.FromSeconds(5), 0.9, animate: false);
        Check(((FrameworkElement)toast.FindName("Root")).LayoutTransform == System.Windows.Media.Transform.Identity && Math.Abs(toast.Width - 400) < 1, "Toast at 1080p keeps its old size");
        toast.Close();
    }

    /// <summary>0.18.4: «Работа с игрой» разбита на группы, у каждой настройки название и пояснение, переключатели по-прежнему сохраняют свои значения.</summary>
    static void SettingsAutomationCheck(AppHost host)
    {
        var view = new SettingsView();
        view.Refresh();
        view.Measure(new Size(1000, 4000)); view.Arrange(new Rect(0, 0, 1000, 4000)); view.UpdateLayout();
        var names = new[] { "AutoWatchBox", "AutoHideBox", "AutoShowBox", "DraftToastsBox", "AutoRoleBox", "AutoModesBox", "LaneTipsBox", "PowerSpikesBox", "ShowBuildBox", "PostMatchBox", "AutoImportBox", "AutoReviewBox" };
        var boxes = names.Select(n => Control<CheckBox>(view, n)).ToList();
        Check(boxes.All(b => b.Content is StackPanel { Children.Count: 2 } sp && sp.Children.OfType<TextBlock>().All(t => t.Text.Length > 0)),
            "Every game automation option has a title and a description");
        var ys = boxes.Select(b => b.TransformToAncestor(view).Transform(new Point()).Y).ToList();
        Check(ys.Zip(ys.Skip(1)).All(p => p.First < p.Second), "Automation options follow the game order: launch, draft, game, after");
        var a = host.Settings.Current.Automation;
        bool before = a.LaneTips;
        Control<CheckBox>(view, "LaneTipsBox").IsChecked = !before;
        Check(host.Settings.Current.Automation.LaneTips == !before, "Regrouped toggle still saves its setting");
        Control<CheckBox>(view, "LaneTipsBox").IsChecked = before;
        Control<CheckBox>(view, "AutoWatchBox").IsChecked = false;
        Check(!Control<CheckBox>(view, "AutoHideBox").IsEnabled && !Control<CheckBox>(view, "AutoShowBox").IsEnabled, "Tray options are disabled without game watching");
        Control<CheckBox>(view, "AutoWatchBox").IsChecked = true;
        Check(Control<Button>(view, "OpenDotaLinkButton").Visibility == Visibility.Visible
              && Control<TextBlock>(view, "OpenDotaNote").Text == Roshiq.Core.Localization.Loc.T("set_opendota_missing"),
            "Without Steam the OpenDota options say why they do not work");
    }

    [STAThread]
    static int Main(string[] args)
    {
        try
        {
            Environment.SetEnvironmentVariable("ROSHIQ_HOME", Path.Combine(Path.GetTempPath(), "Roshiq.UiSmoke", Guid.NewGuid().ToString("N")));
            // Обычный Application с теми же словарями, что в App.xaml: настоящий App при первой прокачке диспетчера
            // выполнил бы OnStartup (проверка единственного экземпляра, окна запуска), а тест должен работать рядом с установленной программой.
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/Roshiq.Overlay;component/Icons.xaml") });
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/Roshiq;component/Theme.xaml") });
            var host = new AppHost();
            typeof(App).GetProperty("Host")!.SetValue(null, host);
            host.Settings.Current.Gsi.Enabled = false;
            MapPolishChecks.Run(args);
            ThemeChecks();
            ToastScaleCheck();
            var main = new MainWindow(); app.MainWindow = main;
            foreach (var h in host.Database.Heroes)
            {
                var nativePortrait = Roshiq.App.Services.HeroArt.Portrait(h.Key) as System.Windows.Media.Imaging.BitmapSource;
                Check(nativePortrait is { PixelWidth: >= 128, PixelHeight: >= 72 } && nativePortrait.IsFrozen, "Native UI portrait: " + h.Key);
            }
            var lich = (System.Windows.Media.Imaging.BitmapSource)Roshiq.App.Services.HeroArt.Portrait("lich")!;
            Check(lich.PixelWidth >= 256, "Large Lich portrait retains native pixels");
            var blink = (System.Windows.Media.Imaging.BitmapSource)Roshiq.App.Services.HeroArt.ItemIcon("item_blink")!;
            Check(blink.PixelWidth >= 88, "Item icons retain native pixels");
            SettingsAutomationCheck(host);
            var history = new HistoryView(); history.Refresh();
            var historyList = Control<ListBox>(history, "MatchList");
            var report = Control<StackPanel>(history, "ReviewPanel");
            report.Visibility = Visibility.Visible;
            report.Children.Add(new Border { Height = 1600 });
            history.Measure(new Size(1160, 520)); history.Arrange(new Rect(0, 0, 1160, 520)); history.UpdateLayout();
            var listOrigin = historyList.TransformToAncestor(history).Transform(new Point());
            Check(listOrigin.Y < 200, "History list stays at the top beside a long report");
            foreach (var page in new UserControl[] { new DashboardView(), new DraftView(), new HeroesView() })
            {
                if (page is IRefreshable refreshable) refreshable.Refresh();
                foreach (var size in new[] { new Size(1160, 520), new Size(940, 380) })
                {
                    page.Measure(size); page.Arrange(new Rect(new Point(), size)); page.UpdateLayout();
                    Check(page.IsMeasureValid && page.IsArrangeValid, page.GetType().Name + " layout " + size);
                }
            }
            var reactiveDraft = new DraftView(); reactiveDraft.Refresh();
            var enemyTiles = Control<System.Windows.Controls.Primitives.UniformGrid>(reactiveDraft, "EnemySlots");
            var enemyBox = ((Panel)enemyTiles.Children[0]).Children.OfType<ComboBox>().Single();
            enemyBox.SelectedItem = enemyBox.Items.OfType<Choice<Hero?>>().First(c => c.Value?.Key == "phantom_assassin");
            Check(host.Draft.Enemies.Any(h => h.Key == "phantom_assassin") && host.LastDraftAnalysis != null, "Manual pick recomputes draft without Analyze click");
            enemyBox.SelectedIndex = 0;
            Check(host.Draft.Enemies.Count == 0, "Clearing a pick clears its draft context");
            var adaptationView = new HeroesView(); adaptationView.Refresh();
            typeof(HeroesView).GetField("_previewEnemies", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(adaptationView,
                new List<Hero> { host.Database.FindHero("phantom_assassin")!, host.Database.FindHero("riki")! });
            adaptationView.Refresh();
            Check(Control<Panel>(adaptationView, "EnemyContextPanel").Children.Count == 2 && Control<Panel>(adaptationView, "AdaptationPanel").Children.Count > 0,
                "Build enemy editor renders context and adaptation advice");
            var summary = new Roshiq.Recommendations.PostMatchSummary(new Roshiq.Recommendations.MatchRecord { HeroId = 1, DurationSec = 1800 },
                Array.Empty<Roshiq.Recommendations.Insight>(), new Roshiq.Recommendations.Insight("no_tp", -2, "Нет ТП", "No TP"), 3);
            var resultWindow = new PostMatchWindow(summary);
            Check(Control<TextBlock>(resultWindow, "NextGoalText").Text.Length > 0, "Post-match window shows next-game action");
            resultWindow.Close();
            var takeaways = new MatchTakeaways(summary);
            Check(takeaways.Children.Count == 4, "History summary includes three takeaways and comparison basis");
            var view = new HeroesView(); view.Refresh();
            var favorites = Control<CheckBox>(view, "FavoritesOnly");
            favorites.IsChecked = true;
            Check(Control<ListBox>(view, "HeroList").Items.Count == 0, "Empty favorites filter");
            favorites.IsChecked = false;
            Control<Button>(view, "FavoriteButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            favorites.IsChecked = true;
            Check(Control<ListBox>(view, "HeroList").Items.OfType<Choice<Hero>>().Count(x => !x.IsHeader) == 1, "Favorite button and filter");
            var hero = ((Choice<Hero>)Control<ListBox>(view, "HeroList").SelectedItem).Value;
            Check(((Choice<Hero>)Control<ListBox>(view, "HeroList").SelectedItem).Display == "★  " + hero.DisplayName(Roshiq.Core.Localization.Loc.Instance.Language), "English hero label without duplicate");
            var build = new PersonalBuild { HeroKey = hero.Key, Position = Position.Carry, Name = "Smoke", Entries = new() {
                new() { ItemKey = "item_tango", Phase = BuildPhase.Start }, new() { ItemKey = "item_branches", Phase = BuildPhase.Start } } };
            // Test real editor XAML, selection handlers, search, row movement and copy isolation without displaying a window.
            var editor = new PersonalBuildEditor(build);
            Control<TextBox>(editor, "ItemSearch").Text = "blink";
            Check(Control<ComboBox>(editor, "ItemBox").Items.Count > 0, "Item search populates results");
            var itemChoice = (Choice<Item>)Control<ComboBox>(editor, "ItemBox").SelectedItem;
            Check(itemChoice.Display == itemChoice.Value.DisplayName(Roshiq.Core.Localization.Loc.Instance.Language), "English item label without duplicate");
            Control<Button>(editor, "AddButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(editor.Result.Entries.Count == 3, "Editor adds item");
            var rows = Control<ListBox>(editor, "EntryList"); rows.SelectedIndex = 0;
            Control<TextBox>(editor, "EntryNote").Text = "Saved note";
            Check(editor.Result.Entries[0].Note == "Saved note", "Item note handler");
            Check(build.Entries[0].Note == "", "Unsaved edits do not mutate source");
            Control<Button>(editor, "DownButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(editor.Result.Entries[1].ItemKey == "item_tango", "Move item down");
            var phases = Control<ComboBox>(editor, "EntryPhase"); phases.SelectedIndex = 2;
            Check(editor.Result.Entries.First(x => x.ItemKey == "item_tango").Phase == BuildPhase.Mid, "Move item to another phase");
            host.Settings.Update(s => s.PersonalBuilds.Add(build)); view.Refresh();
            var box = Control<ComboBox>(view, "PersonalBuildBox"); box.SelectedIndex = 1;
            var actions = new List<Button>();
            Walk(view, actions);
            var use = actions.First(x => string.Equals(x.Content as string, Roshiq.Core.Localization.Loc.T("my_use"), StringComparison.OrdinalIgnoreCase));
            use.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(host.CurrentHero?.Key == hero.Key && host.CurrentBuild?[BuildPhase.Start][0].Item.Key == "item_tango", "Use personal build in app host");
            Check(new SettingsService(host.Settings.FilePath).Current.PersonalBuilds.Count == 1, "UI data survives settings reload");
            Check(host.Vision.Matcher.HeroCount == 127, "Bundled templates load for all 127 heroes");
            using (var data = typeof(App).Assembly.GetManifestResourceStream("Roshiq.App.Assets.Portraits.zip")!)
            using (var archive = new ZipArchive(data))
            {
                int matched = 0;
                foreach (var file in archive.Entries.Where(x => x.FullName.EndsWith("/base.dpt")))
                {
                    using var stream = file.Open();
                    var crop = RgbImage.Load(stream).ResizeArea(62, 35);
                    var match = host.Vision.Matcher.Match(crop);
                    if (match.Status == SlotStatus.Recognized && match.HeroKey == file.FullName.Split('/')[0]) matched++;
                }
                Check(matched == 127, "All bundled portraits match after rescaling to HUD size");
            }
            var onGsi = typeof(AppHost).GetMethod("OnGsi", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var onVision = typeof(AppHost).GetMethod("OnVisionResult", BindingFlags.NonPublic | BindingFlags.Instance)!;
            host.Settings.Current.Position = Position.Offlane;
            var snapshot = new GameStateSnapshot { GameState = "DOTA_GAMERULES_STATE_HERO_SELECTION", TeamName = "radiant", MatchId = "smoke-1" };
            onGsi.Invoke(host, new object[] { snapshot });
            host.RefreshOverlay();
            Check(host.OverlayVm.DraftMode && host.OverlayVm.Section1.Count == 0, "Draft overlay waits for confirmed enemy picks");
            var frame = new RgbImage(1920, 1080);
            using (var data = typeof(App).Assembly.GetManifestResourceStream("Roshiq.App.Assets.Portraits.zip")!)
            using (var archive = new ZipArchive(data))
            {
                foreach (var region in DraftLayout.Compute(host.Settings.Current.Vision.Layout, frame.Width, frame.Height))
                {
                    if (region.Side != Side.Dire || region.Index != 0) continue;
                    using var entry = archive.GetEntry("phantom_lancer/base.dpt")!.Open();
                    var image = RgbImage.Load(entry).ResizeArea(region.Width, region.Height);
                    for (int y = 0; y < region.Height; y++) for (int x = 0; x < region.Width; x++) {
                        var (r, g, b) = image.GetPixel(x, y); frame.SetPixel(region.X + x, region.Y + y, r, g, b);
                    }
                }
            }
            var recognition = host.Vision.Recognizer.Recognize(frame, host.Settings.Current.Vision.Layout) with { SessionId = host.AutoDraftGeneration };
            Check(recognition.For(Side.Dire).First().Match.HeroKey == "phantom_lancer", "Full frame portrait layout and recognition pipeline");
            var portrait = recognition.For(Side.Dire).First().Crop;
            onVision.Invoke(host, new object[] { recognition });
            Check(host.AutoDraftAnalysis == null, "One screen frame cannot produce counterpicks");
            onVision.Invoke(host, new object[] { recognition });
            host.RefreshOverlay();
            Check(host.AutoDraftAnalysis?.Top.Count == 3 && host.OverlayVm.Section1.Count == 3, "Two confirmed frames populate three role-aware overlay counterpicks");
            onGsi.Invoke(host, new object[] { snapshot with { GameState = "DOTA_GAMERULES_STATE_GAME_IN_PROGRESS" } });
            host.RefreshOverlay();
            Check(!host.OverlayVm.DraftMode && host.AutoDraftAnalysis == null, "Gameplay clears automatic draft state");
            onGsi.Invoke(host, new object[] { snapshot with { MatchId = "smoke-2" } });
            onVision.Invoke(host, new object[] { recognition });
            Check(host.AutoDraftAnalysis == null, "Late result from old draft session is ignored");
            onGsi.Invoke(host, new object[] { snapshot with { MatchId = "smoke-2", TeamName = "dire" } });
            var direFrame = recognition with { SessionId = host.AutoDraftGeneration, Slots = new[] {
                new SlotResult(new SlotRegion(Side.Radiant, 0, 0, 0, 4, 4), recognition.For(Side.Dire).First().Match, portrait) } };
            onVision.Invoke(host, new object[] { direFrame }); onVision.Invoke(host, new object[] { direFrame });
            Check(host.AutoEnemies.Single().Key == "phantom_lancer", "Dire player uses Radiant portraits as enemies");
            var draftView = new DraftView(); draftView.Refresh();
            Check(Control<CheckBox>(draftView, "AutoDraftBox").IsChecked == true, "Automatic draft control reflects saved setting");
            var manual = recognition with { IsManual = true, SessionId = host.AutoDraftGeneration };
            onGsi.Invoke(host, new object[] { snapshot with { GameState = "DOTA_GAMERULES_STATE_GAME_IN_PROGRESS" } });
            manual = manual with { SessionId = host.AutoDraftGeneration };
            onVision.Invoke(host, new object[] { manual });
            Check(host.Draft.Enemies.Any(h => h.Key == "phantom_lancer"), "Explicit manual scan still updates manual analysis outside drafting");
            onGsi.Invoke(host, new object[] { snapshot with { MatchId = "smoke-render", TeamName = "radiant" } });
            recognition = recognition with { SessionId = host.AutoDraftGeneration };
            onVision.Invoke(host, new object[] { recognition }); onVision.Invoke(host, new object[] { recognition });
            HistoryUiChecks.Run(host);
            int renderIndex = Array.IndexOf(args, "--render");
            if (renderIndex >= 0 && renderIndex + 1 < args.Length)
            {
                host.RefreshOverlay(); host.OverlayVm.Expanded = true;
                var panel = new Roshiq.Overlay.OverlayPanelWindow { DataContext = host.OverlayVm };
                var content = (FrameworkElement)panel.Content;
                content.Width = 420; content.Height = 420;
                content.Measure(new Size(420, 420)); content.Arrange(new Rect(0, 0, 420, 420)); content.UpdateLayout();
                var image = new System.Windows.Media.Imaging.RenderTargetBitmap(420, 420, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                image.Render(content);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
                using var output = File.Create(args[renderIndex + 1]); encoder.Save(output);
                Console.WriteLine("Rendered draft overlay.");
            }
            Console.WriteLine("UI smoke checks completed.");
            host.Dispose();
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    static void Walk(DependencyObject root, List<Button> buttons)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root))
            if (child is DependencyObject d) { if (d is Button b) buttons.Add(b); Walk(d, buttons); }
    }
}

