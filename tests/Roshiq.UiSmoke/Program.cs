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
    [STAThread]
    static int Main(string[] args)
    {
        try
        {
            Environment.SetEnvironmentVariable("ROSHIQ_HOME", Path.Combine(Path.GetTempPath(), "Roshiq.UiSmoke", Guid.NewGuid().ToString("N")));
            var app = new App(); app.InitializeComponent(); app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var host = new AppHost();
            typeof(App).GetProperty("Host")!.SetValue(null, host);
            host.Settings.Current.Gsi.Enabled = false;
            var main = new MainWindow(); app.MainWindow = main;
            var view = new HeroesView(); view.Refresh();
            var favorites = Control<CheckBox>(view, "FavoritesOnly");
            favorites.IsChecked = true;
            Check(Control<ListBox>(view, "HeroList").Items.Count == 0, "Empty favorites filter");
            favorites.IsChecked = false;
            Control<Button>(view, "FavoriteButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            favorites.IsChecked = true;
            Check(Control<ListBox>(view, "HeroList").Items.Count == 1, "Favorite button and filter");
            var hero = ((Choice<Hero>)Control<ListBox>(view, "HeroList").SelectedItem).Value;
            Check(((Choice<Hero>)Control<ListBox>(view, "HeroList").SelectedItem).Display == "★  " + hero.Name, "English hero label without duplicate");
            var build = new PersonalBuild { HeroKey = hero.Key, Position = Position.Carry, Name = "Smoke", Entries = new() {
                new() { ItemKey = "item_tango", Phase = BuildPhase.Start }, new() { ItemKey = "item_branches", Phase = BuildPhase.Start } } };
            // Test real editor XAML, selection handlers, search, row movement and copy isolation without displaying a window.
            var editor = new PersonalBuildEditor(build);
            Control<TextBox>(editor, "ItemSearch").Text = "blink";
            Check(Control<ComboBox>(editor, "ItemBox").Items.Count > 0, "Item search populates results");
            var itemChoice = (Choice<Item>)Control<ComboBox>(editor, "ItemBox").SelectedItem;
            Check(itemChoice.Display == itemChoice.Value.Name, "English item label without duplicate");
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
            var use = actions.First(x => (x.Content as string) == "Использовать");
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

