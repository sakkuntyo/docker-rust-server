using System.IO;
using System.Net;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RustMonitor;
using RustMonitor.Core;

internal static partial class Program
{
    private sealed class IconHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Interlocked.Increment(ref Requests); return Task.FromResult(respond(request)); }
    }
    private static byte[] FixturePng()
    {
        var image = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 90, 165, 195, 255 }, 4);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = new MemoryStream(); encoder.Save(stream); return stream.ToArray();
    }
    private static void InventoryChecks()
    {
        var cloth = new ItemRecord { Container = "main", Slot = 23, ShortName = "cloth", Amount = 39 };
        var slots = InventoryLayout.Slots([cloth], "main");
        Check(slots.Count == 24 && slots[23].Item == cloth && slots.Take(23).All(s => s.Item == null), "inventory keeps empty slots and original saved slot positions");
        Check(InventoryLayout.Slots([], "belt").Count == 6 && InventoryLayout.Slots([], "wear").Count == 8, "empty belt and equipment retain their slot capacities");
        var duplicate = new ItemRecord { Container = "main", Slot = 23 };
        var invalid = new ItemRecord { Container = "main", Slot = int.MaxValue };
        var extra = InventoryLayout.Slots([cloth, duplicate, invalid], "main");
        Check(extra.Count == 26 && extra.Count(s => s.Item != null) == 3 && extra.Skip(24).All(s => !s.PositionKnown), "duplicate and invalid slots preserve items without inventing positions or allocating huge grids");
        var named = new ItemRecord { ItemId = -151838493, Name = "Custom name" }; ItemCatalog.Name(named);
        Check(named.Name == "Custom name" && named.ShortName == "wood", "catalog resolves icon shortnames while preserving custom item names");
        var saved = new ItemRecord { ItemId = -151838493, ShortName = "-151838493" }; ItemCatalog.Name(saved);
        Check(saved.ShortName == "wood" && ItemIcons.ImageUri(saved.ShortName) != null, "numeric shortname placeholders from SSH saves resolve to real icon names");
        var cached = new ItemRecord { ItemId = 1079279582, Name = "Medical Syringe", ShortName = "1079279582" }; ItemCatalog.Name(cached);
        Check(cached.Name == "Medical Syringe" && cached.ShortName == "syringe.medical", "already-named cached items resolve positive numeric placeholders too");
        Check(InventoryLayout.Durability(new ItemRecord { Condition = 150, MaxCondition = 100 }) == 1 && InventoryLayout.Durability(new ItemRecord { Condition = float.NaN, MaxCondition = 100 }) == 0, "durability bars stay bounded even with invalid saved values");
        Check(ItemIcons.ImageUri("pistol.m92")!.AbsoluteUri == "https://files.facepunch.com/rust/item/pistol.m92_512.png" && new[] { "../cloth", "x/y", "C:\\x", "https://evil.invalid", "" }.All(n => ItemIcons.ImageUri(n) == null), "icon requests stay on the official image host with valid item names");
        var bytes = FixturePng();
        using var handler = new IconHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        using var client = new HttpClient(handler);
        var cache = Path.Combine(root, "icon-cache-checks");
        using (var icons = new ItemIcons(cache, client))
        {
            var images = Task.WhenAll(Enumerable.Range(0, 12).Select(_ => icons.GetAsync("wood"))).GetAwaiter().GetResult();
            Check(handler.Requests == 1 && images.All(i => i?.IsFrozen == true), "concurrent icon requests share one download and return thread-safe decoded images");
            Check(File.Exists(Path.Combine(cache, "wood.png")), "successfully decoded icons are cached on disk");
        }
        using var offlineHandler = new IconHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var offline = new HttpClient(offlineHandler);
        using (var icons = new ItemIcons(cache, offline))
            Check(icons.GetAsync("wood").GetAwaiter().GetResult() != null && offlineHandler.Requests == 0, "cached icons load on subsequent launches without network access");
        File.WriteAllBytes(Path.Combine(cache, "cloth.png"), bytes[..24]);
        using (var icons = new ItemIcons(cache, client))
            Check(icons.GetAsync("cloth").GetAwaiter().GetResult() != null && handler.Requests == 2, "corrupt cached PNGs are recovered by downloading a valid image");
        foreach (var (name, payload, status) in new[] {
            ("missing", bytes, HttpStatusCode.NotFound), ("truncated", bytes[..24], HttpStatusCode.OK),
            ("oversized", new byte[2 * 1024 * 1024 + 1], HttpStatusCode.OK) })
        {
            using var badHandler = new IconHandler(_ => new HttpResponseMessage(status) { Content = new ByteArrayContent(payload) });
            using var badClient = new HttpClient(badHandler); using var icons = new ItemIcons(cache, badClient);
            Check(icons.GetAsync(name).GetAwaiter().GetResult() == null && !File.Exists(Path.Combine(cache, name + ".png")), "unavailable or invalid icon falls back without caching: " + name);
        }
    }
    private static ItemIcons PrepareRenderIcons(string directory)
    {
        var cache = Path.Combine(directory, "icons"); Directory.CreateDirectory(cache);
        // Optional local official images for visual review; ordinary tests are offline.
        var source = Environment.GetEnvironmentVariable("RUST_MONITOR_TEST_ICON_CACHE");
        var icons = new ItemIcons(cache, new HttpClient(new IconHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound))));
        var bytes = FixturePng();
        foreach (var name in new[] { "wood", "metal.fragments", "cloth", "rifle.ak", "knife.bone", "syringe.medical", "hazmatsuit" })
        {
            var existing = source == null ? "" : Path.Combine(source, name + ".png");
            File.WriteAllBytes(Path.Combine(cache, name + ".png"), File.Exists(existing) ? File.ReadAllBytes(existing) : bytes);
            icons.GetAsync(name).GetAwaiter().GetResult();
        }
        return icons;
    }
    private static void LiveIconChecks()
    {
        // Exercise the production catalog -> download -> dispatcher -> Image path,
        // starting from the same numeric shortname placeholders as native saves.
        var fixture = Path.Combine(root, "live-icons-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        var app = new App(); app.InitializeComponent();
        SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(app.Dispatcher));
        var profile = new SshProfile("admin@demo.invalid", "rust-demo");
        var state = new ServerState { WipeId = "icon-check", Name = "DEMO / 画像取得の検証", CapturedAt = DateTimeOffset.UtcNow.ToString("O") };
        var player = new PlayerRecord { SteamId = "76561198000000001", Name = "Demo Player", WipeId = state.WipeId };
        var ids = new[] { 1079279582, 317398316, -1211166256, -151838493, 1545779598, 1266491000 };
        var names = new[] { "syringe.medical", "metal.refined", "ammo.rifle", "wood", "rifle.ak", "hazmatsuit" };
        using (var db = new Store(Path.Combine(fixture, "monitor.sqlite3")))
        {
            db.Put("last-ssh-profile", Wire.Write(profile)); db.SaveRoster(profile.Key, state, [player]);
            db.SaveInventory(profile.Key, new InventorySnapshot { SteamId = player.SteamId, WipeId = state.WipeId, CapturedAt = state.CapturedAt,
                Items = ids.Select((id, slot) => new ItemRecord { ItemId = id, ShortName = id.ToString(), Slot = slot, Container = "main", Amount = slot + 1 }).ToList() });
        }
        Check(!Directory.Exists(Path.Combine(fixture, "icons")), "live icon check starts with no cached images");
        var window = new MainWindow(fixture);
        ((ListBox)window.FindName("PlayerList")).SelectedIndex = 0;
        var panel = (StackPanel)window.FindName("InventoryItems");
        var cells = ((UniformGrid)panel.Children.OfType<Viewbox>().First().Child).Children.OfType<Border>().Take(ids.Length).ToArray();
        var images = cells.Select(c => ((Grid)c.Child).Children.OfType<Image>().Single()).ToArray();
        var frame = new System.Windows.Threading.DispatcherFrame();
        var start = DateTime.UtcNow;
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        timer.Tick += (_, _) => { if (images.All(i => i.Source != null) || DateTime.UtcNow - start > TimeSpan.FromSeconds(35)) frame.Continue = false; };
        timer.Start(); System.Windows.Threading.Dispatcher.PushFrame(frame); timer.Stop();
        for (var i = 0; i < names.Length; i++)
            Check(images[i].Source != null && File.Exists(Path.Combine(fixture, "icons", names[i] + ".png")), "live download and WPF image assignment: " + names[i]);
        var content = (FrameworkElement)window.Content;
        content.Measure(new Size(1872, 1000)); content.Arrange(new Rect(0, 0, 1872, 1000)); content.UpdateLayout();
        SaveRender(content, "live-icons-preview.png", 1872, 1000);
        window.Close();
        Console.WriteLine("Live icon cache: " + Path.Combine(fixture, "icons"));
    }
    private static void InventoryRenderChecks(MainWindow window)
    {
        var panel = (StackPanel)window.FindName("InventoryItems");
        var grids = panel.Children.OfType<Viewbox>().Select(v => (UniformGrid)v.Child).ToList();
        Check(grids.Select(g => g.Children.Count).SequenceEqual(new[] { 24, 6, 8 }), "WPF shows all main, belt, and equipment slots");
        var nativeCells = new[] { (Border)grids[0].Children[0], (Border)grids[1].Children[3] };
        Check(nativeCells.All(c => ((Grid)c.Child).Children.OfType<Image>().Single().Source != null), "WPF resolves images from raw native-save and already-named cached item records");
        var cloth = (Border)grids[0].Children[23];
        Check(((InventorySlot)cloth.Tag).Item!.Amount == 39 && ((Grid)cloth.Child).Children.OfType<Image>().Single().Source != null, "the saved 24th slot shows its icon and stack amount");
        var rifle = (Border)grids[1].Children[0];
        var tooltip = (StackPanel)rifle.ToolTip;
        var details = string.Join("\n", tooltip.Children.OfType<TextBlock>().Select(t => t.Text));
        Check(details.Contains("装填数 23") && details.Contains("Holosight") && details.Contains("画像は標準外観"), "hover details retain ammo, nested attachments, and custom skin caveat");
        var content = (FrameworkElement)window.Content;
        content.Measure(new Size(1872, 1000)); content.Arrange(new Rect(0, 0, 1872, 1000)); content.UpdateLayout();
        SaveRender(content, "inventory-preview.png", 1872, 1000);
        var scroll = (ScrollViewer)window.FindName("InventoryScroll");
        var expandedHeight = scroll.ActualHeight;
        var toggle = (Button)window.FindName("InventoryDetailsToggle");
        var name = ((TextBox)window.FindName("InventoryName")).Text;
        toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); content.UpdateLayout();
        Check(scroll.ActualHeight > expandedHeight + 100 && ((TextBox)window.FindName("InventoryName")).Text == name,
            "hiding player information gives the inventory more vertical space while keeping the selected name");
        SaveRender(content, "inventory-collapsed-preview.png", 1872, 1000);
        var restored = new MainWindow(Path.Combine(root, "ui-fixture"));
        Check(((FrameworkElement)restored.FindName("InventoryDetailsPanel")).Visibility == Visibility.Collapsed,
            "the hidden information setting survives reopening the window");
        restored.Close();
        // Show the same tooltip template without interacting with the user's desktop.
        var tip = new ToolTip { Content = tooltip };
        tip.Measure(new Size(340, 600)); tip.Arrange(new Rect(new Point(0, 0), tip.DesiredSize)); tip.UpdateLayout();
        SaveRender(tip, "inventory-tooltip-preview.png", (int)Math.Ceiling(tip.ActualWidth), (int)Math.Ceiling(tip.ActualHeight));
        tip.Content = null;
        var list = (ListBox)window.FindName("PlayerList"); list.SelectedIndex = 1;
        Check(panel.Children.OfType<Viewbox>().Count() == 0, "a player with no inventory snapshot never appears to have an empty inventory");
        content.UpdateLayout();
        var notice = (TextBox)window.FindName("InventoryNotice");
        Check(notice.Visibility == Visibility.Visible && notice.Text.Contains("未取得"), "missing inventory remains explained when player information is hidden");
        list.SelectedIndex = 0;
        content.UpdateLayout();
        Check(scroll.ActualHeight > expandedHeight + 100 && notice.Visibility == Visibility.Collapsed,
            "selecting another recorded player preserves the extra inventory space");
        toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); content.UpdateLayout();
        Check(Math.Abs(scroll.ActualHeight - expandedHeight) < 1 && ((TextBox)window.FindName("PlayerSteamId")).Text.Contains("Steam ID:"),
            "showing information again restores the details and original inventory viewport");
        content.Measure(new Size(1392, 784)); content.Arrange(new Rect(0, 0, 1392, 784)); content.UpdateLayout();
    }
    private static void SaveRender(Visual content, string name, int width, int height)
    {
        var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); image.Render(content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var file = File.Create(Path.Combine(root, name)); encoder.Save(file);
    }
}
