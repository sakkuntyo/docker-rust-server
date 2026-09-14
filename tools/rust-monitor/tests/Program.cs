using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RustMonitor;
using RustMonitor.Core;

internal static class Program
{
    private static int checks;
    private static string root = "";
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); checks++; Console.WriteLine("PASS " + message); }
    [STAThread]
    private static int Main(string[] args)
    {
        root = Path.GetFullPath(args[0]); Directory.CreateDirectory(root);
        try
        {
            if (args.Length == 4 && args[1] == "--live-ssh")
            {
                LiveSshCheck(new SshProfile(args[2], args[3]));
                return 0;
            }
            if (args.Length == 3 && args[1] == "--live-overview")
            {
                var report = DockerSsh.ReadOverviewAsync(args[2]).GetAwaiter().GetResult();
                File.WriteAllText(Path.Combine(root, "server-status-latest.json"), Wire.Write(report));
                Check(report.Servers.Count > 0 && report.Servers.All(s => s.Capacity.HasValue && s.Name.Length > 0), "desktop SSH transport reads aggregate metadata even with partial startup results");
                Console.WriteLine(Wire.Write(report)); return 0;
            }
            DatabaseChecks(); SshSnapshotChecks(); MapChecks(); RconChecks().GetAwaiter().GetResult(); RenderChecks(args.Length > 1 ? args[1] : null); Console.WriteLine($"{checks} checks passed."); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static void DatabaseChecks()
    {
        var path = Path.Combine(root, "checks.sqlite3");
        var player = new PlayerRecord { SteamId = "76561198012345678", Name = "日本語 ' ; DROP TABLE players; --", FirstSeen = "2026-09-01T00:00:00Z", Online = true };
        using (var db = new Store(path))
        {
            db.Put("unicode", "日本語 🦀"); db.Put("empty", "");
            db.SaveRoster("server-a", new ServerState { WipeId = "wipe-a" }, [player]);
            db.SaveRoster("server-b", new ServerState { WipeId = "wipe-b" }, [new PlayerRecord { SteamId = player.SteamId, Name = "別サーバー" }]);
            db.SaveInventory("server-a", new InventorySnapshot { SteamId = player.SteamId, WipeId = "wipe-a", CapturedAt = "2026-09-14T00:00:00Z", Items = [new ItemRecord { ShortName = "rifle.ak", Amount = 1, Ammo = 23, Contents = [new ItemRecord { ShortName = "weapon.mod.holosight", Amount = 1 }] }] });
            Check(db.Get("empty") == "", "empty text is stored as text");
            Check(db.Inventory("server-a", "wipe-b", player.SteamId) == null, "previous wipe inventory cannot appear in the new wipe");
            Check(db.Inventory("server-b", "wipe-a", player.SteamId) == null, "server inventory isolation");
        }
        using (var db = new Store(path))
        {
            Check(db.Get("unicode") == "日本語 🦀", "SQLite persistence and Unicode round trip");
            Check(db.Players("server-a").Single().SteamId == player.SteamId, "Steam ID preserves all 64 bits as text");
            Check(db.Players("server-a").Single().Name == player.Name, "player names are bound parameters, including SQL-like input");
            Check(db.Players("server-b").Single().Name == "別サーバー", "server roster isolation");
            Check(db.Inventory("server-a", "wipe-a", player.SteamId)!.Items.Single().Contents.Single().ShortName == "weapon.mod.holosight", "nested inventory persistence");
        }
        Check(!new MainWindow.PlayerRow(player, false).IsOnline, "cached online state is never presented as live");
        player.ObservedAt = DateTimeOffset.UtcNow.AddHours(-1).ToString("O");
        Check(!new MainWindow.PlayerRow(player, true).IsOnline, "stale observations are unknown even while connected");
        player.ObservedAt = DateTimeOffset.UtcNow.ToString("O");
        Check(new MainWindow.PlayerRow(player, true).IsOnline, "fresh connected observation is online");
        var uri = new ConnectionProfile("localhost", 28016, false).Uri("p?ss#secret");
        Check(uri.Query == "" && uri.Fragment == "" && uri.AbsolutePath.Contains("%3F"), "password punctuation stays in the WebRCON path");
        Check(DockerSsh.ValidTarget("admin@docker-host") && !DockerSsh.ValidTarget("-oProxyCommand=evil") && !DockerSsh.ValidTarget("host; cmd"), "SSH target cannot add options or shell commands");
        using (var db = new Store(path))
        {
            DockerSsh.Remember(db, "admin@docker-host", new DockerReport { Servers = [new DockerServer { Container = "rust-test", SeasonEnd = "2026-09-19T00:00:00Z", Peak = 25, PeakAt = "2026-09-14T01:00:00Z" }] });
            var lower = new DockerReport { Servers = [new DockerServer { Container = "rust-test", SeasonEnd = "2026-09-19T00:00:00Z", Peak = 7 }] };
            DockerSsh.Remember(db, "admin@docker-host", lower);
            Check(lower.Servers[0].Peak == 25 && lower.Servers[0].PeakFromSaved, "observed peak survives container log loss");
            var tied = new DockerReport { Servers = [new DockerServer { Container = "rust-test", SeasonEnd = "2026-09-19T00:00:00Z", Peak = 25, PeakAt = "2026-09-15T01:00:00Z" }] };
            DockerSsh.Remember(db, "admin@docker-host", tied);
            Check(tied.Servers[0].PeakAt == "2026-09-14T01:00:00Z", "equal peaks preserve their first observed timestamp");
            var newSeason = new DockerReport { Servers = [new DockerServer { Container = "rust-test", SeasonEnd = "2026-09-26T00:00:00Z", Peak = 2 }] };
            DockerSsh.Remember(db, "admin@docker-host", newSeason);
            Check(newSeason.Servers[0].Peak == 2, "peak retention is isolated by season");
            var waiting = new DockerReport { Servers = [new DockerServer { Container = "rust-test", SeasonEnd = "2026-09-26T00:00:00Z", Error = "RCON 接続待ち", RconStatus = "unavailable", HistoryStatus = "missing", Capacity = 100 }] };
            DockerSsh.Remember(db, "admin@docker-host", waiting);
            Check(waiting.Servers[0].Peak == 2 && waiting.Servers[0].PeakText.StartsWith("2 人"), "RCON failure preserves and displays same-season peak");
            Check(waiting.Servers[0].CurrentText == "応答待ち" && waiting.Servers[0].SeasonUniqueText == "履歴未作成", "missing history and unavailable RCON have independent status labels");
            var starting = new DockerReport { Servers = [new DockerServer { Container = "rust-test", Name = "rust-test", Error = "再起動中" }] };
            DockerSsh.Remember(db, "admin@docker-host", starting);
            Check(starting.Servers[0].CapacityText == "100（前回）" && starting.Servers[0].Peak == null && starting.Servers[0].Current == null, "restart fallback labels saved capacity and never assumes a season or zero players");
        }
        Check(new DockerServer { Current = 0, SeasonUnique = 0, Peak = 0, Error = "RCON 以外の警告" }.CurrentText == "0", "zero current players remains a valid displayed value");
    }
    private static void MapChecks()
    {
        var player = new PlayerRecord { WipeId = "one", X = 0, Z = 0, PositionAt = "2026-09-15T00:00:00Z" };
        Check(MapViewport.Project(player, "one", 4000, 3000, 3000) == new Point(.5, .5), "world origin maps to image center including ocean padding");
        player.X = -2000; player.Z = 2000;
        var corner = MapViewport.Project(player, "one", 4000, 3000, 3000)!.Value;
        Check(Math.Abs(corner.X * 3000 - 500) < .001 && Math.Abs(corner.Y * 3000 - 500) < .001, "northwest land corner lies inside the 500-pixel ocean border");
        Check(MapViewport.Project(player, "two", 4000, 3000, 3000) == null, "previous wipe coordinates never appear on a new map");
        player.X = double.NaN;
        Check(MapViewport.Project(player, "one", 4000, 3000, 3000) == null, "invalid coordinates never create a marker");
        player.X = 90000;
        Check(MapViewport.Project(player, "one", 4000, 3000, 3000) == null, "positions beyond the map are not clamped into false edge locations");
        player.X = 0; player.PositionAt = "";
        Check(MapViewport.Project(player, "one", 4000, 3000, 3000) == null, "untimed legacy coordinates are not shown as saved positions");
        var map = new MapViewport(); map.Resize(new Size(600, 400), new Size(3000, 3000));
        Check(map.ImageRect == new Rect(100, 0, 400, 400), "map fits without stretching in a rectangular viewport");
        var anchor = new Point(360, 180); var normalized = new Point(.65, .45);
        map.ZoomAt(2, anchor);
        Check((map.ToScreen(normalized) - anchor).Length < .001, "wheel zoom preserves the position under the pointer");
        var before = map.ToScreen(normalized); map.Pan(new Vector(-30, 20));
        Check((map.ToScreen(normalized) - before - new Vector(-30, 20)).Length < .001, "drag moves image and positions by the same distance");
        map.Pan(new Vector(10000, -10000));
        Check(map.ImageRect.Left <= 0 && map.ImageRect.Right >= 600 && map.ImageRect.Top <= 0 && map.ImageRect.Bottom >= 400, "panning is bounded by map edges");
        map.ZoomAt(100, new Point(300, 200)); Check(map.Zoom == 16, "zoom is bounded to a usable maximum");
        map.Reset(); Check(map.Zoom == 1 && map.Center == new Point(.5, .5), "reset restores the whole map");
    }
    private static void LiveSshCheck(SshProfile profile)
    {
        using (var db = new Store(Path.Combine(root, "monitor.sqlite3")))
        {
            db.Put("last-ssh-profile", Wire.Write(profile));
            db.Put("last-ssh-target", profile.Target);
        }
        var app = new App(); app.InitializeComponent();
        var window = new MainWindow(root);
        var frame = new System.Windows.Threading.DispatcherFrame();
        var deadline = DateTime.UtcNow.AddSeconds(100);
        var poll = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        poll.Tick += (_, _) =>
        {
            var status = ((TextBlock)window.FindName("StatusText")).Text;
            if (status.StartsWith("SSH 同期") || status.StartsWith("更新できませんでした") || DateTime.UtcNow > deadline)
                frame.Continue = false;
        };
        app.Dispatcher.BeginInvoke(() => ((Button)window.FindName("ConnectButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
        poll.Start(); System.Windows.Threading.Dispatcher.PushFrame(frame); poll.Stop();
        var statusText = ((TextBlock)window.FindName("StatusText")).Text;
        Console.WriteLine(statusText);
        Check(statusText.StartsWith("SSH 同期"), "actual SSH button completes synchronization");
        var list = (ListBox)window.FindName("PlayerList");
        using (var db = new Store(Path.Combine(root, "monitor.sqlite3")))
        {
            var state = Wire.Read<ServerState>(db.Get("server:" + profile.Key)!);
            var available = db.Players(profile.Key).Select(p => db.Inventory(profile.Key, state.WipeId, p.SteamId)).Where(i => i != null).ToList();
            Console.WriteLine($"Members={list.Items.Count}; Inventories={available.Count}; Items={available.Sum(i => i!.Items.Count)}; SaveAt={state.SaveAt}");
            Check(list.Items.Count > 0 && available.Count > 0, "actual SSH reads and persists players and saved inventories");
            var selected = available.FirstOrDefault(i => i!.Items.Count > 0)?.SteamId;
            list.SelectedItem = list.Items.Cast<MainWindow.PlayerRow>().FirstOrDefault(p => p.SteamId == selected);
        }
        Check(((Image)window.FindName("MapImage")).Source != null, "actual SSH downloads and renders the current map");
        var content = (FrameworkElement)window.Content;
        content.Measure(new Size(1392, 784)); content.Arrange(new Rect(0, 0, 1392, 784)); content.UpdateLayout();
        var markers = (Canvas)window.FindName("MapMarkers");
        Console.WriteLine(((TextBlock)window.FindName("MapPositionStatus")).Text);
        Check(markers.Children.OfType<Button>().Any(), "actual saved player coordinates are drawn on the map");
        var marker = markers.Children.OfType<Button>().First();
        var id = marker.Tag as string;
        marker.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check((list.SelectedItem as MainWindow.PlayerRow)?.SteamId == id && ((TextBlock)window.FindName("PlayerDetails")).Text.Contains("X "), "actual map marker selects member coordinates and inventory");
        content.UpdateLayout();
        var rendered = new RenderTargetBitmap(1440, 832, 96, 96, PixelFormats.Pbgra32); rendered.Render(content);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(rendered));
        using (var file = File.Create(Path.Combine(root, "ssh-live-preview.png"))) png.Save(file);
        ((Button)window.FindName("MapFocus")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        content.UpdateLayout();
        Check(((TextBlock)window.FindName("MapZoomText")).Text == "300%", "selected actual player can be centered at 300 percent zoom");
        var zoomed = new RenderTargetBitmap(1440, 832, 96, 96, PixelFormats.Pbgra32); zoomed.Render(content);
        var zoomPng = new PngBitmapEncoder(); zoomPng.Frames.Add(BitmapFrame.Create(zoomed));
        using (var file = File.Create(Path.Combine(root, "ssh-live-zoom-preview.png"))) zoomPng.Save(file);
        window.Close();
    }
    private static void SshSnapshotChecks()
    {
        Check(DockerSsh.ValidContainer("rust-test") && !DockerSsh.ValidContainer("rust-test; reboot") && !DockerSsh.ValidContainer("--privileged"), "SSH container cannot inject shell commands or Docker options");
        var state = Wire.Read<ServerState>("{\"Seed\":\"1789171597022\"}");
        Check(state.Seed == 1789171597022, "SSH seed values retain their full integer range");
        using var db = new Store(Path.Combine(root, "ssh-snapshot.sqlite3"));
        var profile = new SshProfile("admin@demo.invalid", "rust-test");
        const string id = "76561198012345678";
        var first = new SshServerSnapshot
        {
            Server = new ServerState { Protocol = 1, WipeId = "one", CapturedAt = "2026-09-15T00:00:00Z" }, PresenceAvailable = true,
            Players = [new PlayerRecord { SteamId = id, Name = "Test", WipeId = "one", Online = true, FirstSeen = "2026-09-14T00:00:00Z", LastSeen = "2026-09-15T00:00:00Z", X = 0, Y = 1, Z = -23, PositionAt = "2026-09-14T23:50:00Z" }],
            Inventories = [new InventorySnapshot { SteamId = id, WipeId = "one", CapturedAt = "2026-09-14T23:50:00Z", Source = "save", Items = [new ItemRecord { ItemId = -151838493, Container = "main", Amount = 1000 }] }]
        };
        db.SaveSshSnapshot(profile, first);
        Check(db.Players(profile.Key).Single().Z == -23 && db.Players(profile.Key).Single().PositionAt == first.Players[0].PositionAt, "saved coordinates and their own timestamp persist in SQLite");
        var partial = new SshServerSnapshot { Server = first.Server };
        db.SaveSshSnapshot(profile, partial);
        Check(db.Players(profile.Key).Single().X == null && db.Players(profile.Key).Single().PositionAt == "", "missing current save does not leave a stale map marker");
        Check(db.Players(profile.Key).Single().ObservedAt == "" && db.Inventory(profile.Key, "one", id)!.Items.Count == 1, "failed presence and save reads retain historical inventory without claiming online presence");
        var next = new SshServerSnapshot { Server = new ServerState { Protocol = 1, WipeId = "two", CapturedAt = "2026-09-16T00:00:00Z" }, PresenceAvailable = true };
        db.SaveSshSnapshot(profile, next);
        Check(db.Players(profile.Key).Count == 1 && db.Inventory(profile.Key, "two", id) == null, "new SSH season retains identities but does not reuse previous inventory");
        var invalid = new SshServerSnapshot { Server = new ServerState { Protocol = 1, WipeId = "three" }, Inventories = first.Inventories };
        try { db.SaveSshSnapshot(profile, invalid); Check(false, "mismatched SSH snapshot"); }
        catch (InvalidDataException) { Check(Wire.Read<ServerState>(db.Get("server:" + profile.Key)!).WipeId == "two", "mismatched SSH inventories are rejected before modifying the database"); }
        var wood = new ItemRecord { ItemId = -151838493 };
        ItemCatalog.Name(wood);
        Check(wood.ShortName == "wood", "saved item IDs resolve through the bundled item catalog");
    }
    private static async Task RconChecks()
    {
        var collectorAvailable = true;
        var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders(); builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0));
        await using var host = builder.Build(); host.UseWebSockets();
        host.Run(async context =>
        {
            if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            try
            {
                var buffer = new byte[16384];
                while (socket.State == WebSocketState.Open)
                {
                    var request = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                    if (request.MessageType == WebSocketMessageType.Close) return;
                    using var doc = JsonDocument.Parse(buffer.AsMemory(0, request.Count));
                    var id = doc.RootElement.GetProperty("Identifier").GetInt32();
                    var command = doc.RootElement.GetProperty("Message").GetString();
                    var log = Encoding.UTF8.GetBytes(Wire.Write(new { Identifier = 0, Message = "unrelated console log" }));
                    await socket.SendAsync(new ArraySegment<byte>(log), WebSocketMessageType.Text, true, CancellationToken.None);
                    if (!collectorAvailable && command!.StartsWith("rustmonitor.", StringComparison.Ordinal)) continue;
                    var content = command == "find rustmonitor.server" ? (collectorAvailable ? "rustmonitor.server" : "No results found.")
                        : command == "rustmonitor.server" ? Wire.Write(new ServerState { Protocol = 1, Name = "日本語サーバー", WipeId = "test-wipe", Size = 3500 }) : command == "rustmonitor.missing" ? "Unknown command: rustmonitor.missing" : "{\"Error\":\"Map unavailable\"}";
                    var response = Encoding.UTF8.GetBytes(Wire.Write(new { Identifier = id, Message = content }));
                    var mid = response.Length / 2;
                    await socket.SendAsync(new ArraySegment<byte>(response, 0, mid), WebSocketMessageType.Text, false, CancellationToken.None);
                    await socket.SendAsync(new ArraySegment<byte>(response, mid, response.Length - mid), WebSocketMessageType.Text, true, CancellationToken.None);
                }
            }
            catch (WebSocketException) { }
        });
        await host.StartAsync();
        var address = host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var client = new RconClient();
        await client.ConnectAsync(new ConnectionProfile("127.0.0.1", new Uri(address).Port, false), "test-only-password");
        await client.EnsureCollectorAsync();
        Check(client.Connected, "collector preflight accepts a registered command over WebRCON");
        var state = await client.ReadAsync<ServerState>("rustmonitor.server");
        Check(state.Name == "日本語サーバー", "WebRCON handles fragmented UTF-8 responses and ignores unsolicited logs");
        var concurrent = await Task.WhenAll(client.ReadAsync<ServerState>("rustmonitor.server"), client.ReadAsync<ServerState>("rustmonitor.server"));
        Check(concurrent.All(s => s.WipeId == "test-wipe"), "concurrent UI requests are serialized and correlated");
        try { await client.ReadAsync<ServerState>("rustmonitor.missing"); Check(false, "missing plugin"); }
        catch (InvalidDataException ex) { Check(ex.Message.Contains("プラグイン"), "missing plugin has an actionable error"); }
        try { await client.ReadAsync<ServerState>("rustmonitor.map"); Check(false, "plugin error"); }
        catch (InvalidDataException ex) { Check(ex.Message.Contains("Map unavailable"), "structured plugin error is handled"); }
        collectorAvailable = false;
        try { await client.EnsureCollectorAsync().WaitAsync(TimeSpan.FromSeconds(3)); Check(false, "missing collector preflight"); }
        catch (CollectorUnavailableException ex)
        { Check(client.Connected && ex.Message.Contains("未導入"), "missing collector is detected promptly without misreporting a connection failure"); }
        client.Dispose();
        await host.StopAsync();
    }
    private static void RenderChecks(string? mapPath)
    {
        var app = new App(); app.InitializeComponent();
        var uiRoot = Path.Combine(root, "ui-fixture"); Directory.CreateDirectory(uiRoot);
        var profile = new SshProfile("admin@demo.invalid", "rust-demo");
        var state = new ServerState { Name = "DEMO / UI 検証用データ", WipeId = "fixture-wipe", Size = 3500, Seed = 123456, CapturedAt = "2026-09-14T01:12:00Z" };
        var roster = new[] {
            new PlayerRecord { SteamId = "76561198000000001", Name = "Demo Player A", Online = true, WipeId = state.WipeId, X = -320, Y = 12, Z = 520, PositionAt = state.CapturedAt, FirstSeen = state.CapturedAt, LastSeen = state.CapturedAt },
            new PlayerRecord { SteamId = "76561198000000002", Name = "サンプルプレイヤー", Online = false, WipeId = state.WipeId, X = 710, Y = 0, Z = -290, PositionAt = state.CapturedAt, FirstSeen = state.CapturedAt, LastSeen = state.CapturedAt }
        };
        state.SaveAt = state.CapturedAt;
        using (var db = new Store(Path.Combine(uiRoot, "monitor.sqlite3")))
        {
            db.Put("last-ssh-profile", Wire.Write(profile)); db.Put("ssh-profiles", Wire.Write(new[] { profile })); db.SaveRoster(profile.Key, state, roster);
            db.SaveInventory(profile.Key, new InventorySnapshot { SteamId = roster[0].SteamId, WipeId = state.WipeId, CapturedAt = state.CapturedAt, Current = true, Items = [
                new ItemRecord { Container = "main", Name = "Wood", ShortName = "wood", Amount = 2500 },
                new ItemRecord { Container = "main", Name = "Metal Fragments", ShortName = "metal.fragments", Amount = 650, Slot = 1 },
                new ItemRecord { Container = "belt", Name = "Assault Rifle", ShortName = "rifle.ak", Amount = 1, Condition = 74, MaxCondition = 100, Ammo = 23 },
                new ItemRecord { Container = "wear", Name = "Hazmat Suit", ShortName = "hazmatsuit", Amount = 1 }
            ] });
        }
        if (mapPath != null)
        {
            var target = Path.Combine(uiRoot, "maps", Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(profile.Key + "\n" + state.WipeId))) + ".jpg");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(mapPath, target, true);
        }
        var window = new MainWindow(uiRoot);
        if (mapPath == null)
        {
            var fixtureImage = BitmapSource.Create(3000, 3000, 96, 96, PixelFormats.Gray8, null, new byte[3000 * 3000], 3000);
            fixtureImage.Freeze(); ((Image)window.FindName("MapImage")).Source = fixtureImage;
            ((FrameworkElement)window.FindName("MapEmpty")).Visibility = Visibility.Collapsed;
        }
        var list = (ListBox)window.FindName("PlayerList"); list.SelectedIndex = 0;
        var content = (FrameworkElement)window.Content;
        content.Measure(new Size(1392, 784)); content.Arrange(new Rect(0, 0, 1392, 784)); content.UpdateLayout();
        var image = new RenderTargetBitmap(1440, 832, 96, 96, PixelFormats.Pbgra32); image.Render(content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using (var file = File.Create(Path.Combine(root, "ui-preview.png"))) encoder.Save(file);
        Check(list.Items.Count == 2, "WPF loads persisted roster without a connection");
        Check(((TextBlock)window.FindName("InventoryStatus")).Text.Contains("最終取得"), "WPF labels cached inventory as historical");
        var markers = (Canvas)window.FindName("MapMarkers");
        Check(markers.Children.OfType<Button>().Count() == 2, "WPF draws online and offline saved player coordinates");
        markers.Children.OfType<Button>().First(b => (string)b.Tag == roster[1].SteamId).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check((list.SelectedItem as MainWindow.PlayerRow)?.SteamId == roster[1].SteamId && ((TextBlock)window.FindName("PlayerDetails")).Text.Contains("Z -290.0"), "map marker click selects the correct player and shows coordinates");
        var width = ((Image)window.FindName("MapImage")).Width;
        ((Button)window.FindName("MapZoomIn")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(Math.Abs(((Image)window.FindName("MapImage")).Width / width - 1.5) < .001, "WPF zoom button enlarges the displayed map");
        typeof(MainWindow).GetMethod("LoadMap", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(window, null);
        Check(((TextBlock)window.FindName("MapZoomText")).Text == "150%", "same-season refresh preserves zoom");
        ((Button)window.FindName("MapReset")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(((Image)window.FindName("MapImage")).Width == width, "WPF whole-map button restores the fitted image");
        ((TextBox)window.FindName("SearchBox")).Text = "サンプル";
        Check(list.Items.Count == 1, "WPF Japanese name search");
        Check(markers.Children.OfType<Button>().Count() == 1, "map markers follow the roster search");
        ((CheckBox)window.FindName("OnlineOnly")).IsChecked = true;
        Check(list.Items.Count == 0, "WPF online filter excludes cached presence");
        Check(markers.Children.OfType<Button>().Count() == 0, "empty filtered roster leaves no stale markers");
        using (var db = new Store(Path.Combine(uiRoot, "overview.sqlite3")))
        {
            DockerSsh.Remember(db, "admin@demo.invalid", new DockerReport { CheckedAt = state.CapturedAt, Servers = [
                new DockerServer { Container = "rust-demo", Name = "DEMO / Monthly", RconPort = 28016, Current = 11, SeasonUnique = 304, Capacity = 200, Peak = 24, PeakAt = "2026-09-09T14:30:50.083520Z", SeasonStart = "2026-08-29T00:00:00Z", SeasonEnd = "2026-10-03T00:00:00Z", PartialLogs = true, Samples = 358 },
                new DockerServer { Container = "rust-starting", Name = "DEMO / Starting", RconPort = 31016, Current = null, SeasonUnique = 606, Capacity = 200, Peak = 13, PeakAt = "2026-09-08T14:30:50Z", SeasonStart = "2026-08-21T12:00:00Z", SeasonEnd = "2026-09-25T12:00:00Z", RconStatus = "timeout", Error = "RCON 応答待ち（タイムアウト）" },
                new DockerServer { Container = "rust-wiping", Name = "DEMO / Daily", RconPort = 29016, Current = 0, SeasonUnique = null, Capacity = 100, Peak = null, SeasonStart = "2026-09-14T12:00:00Z", SeasonEnd = "2026-09-15T12:00:00Z", RconStatus = "ok", HistoryStatus = "missing", Error = "今季履歴は未作成（定期記録待ち）" }
            ] });
            var overview = new DockerOverviewWindow(db);
            Check(((DataGrid)overview.FindName("ServersGrid")).Items.Count == 3, "WPF Docker overview loads normal, restarting, and wiped servers");
            Check(((TextBlock)overview.FindName("StatusText")).Text.StartsWith("保存済み"), "WPF Docker overview identifies cached counts");
            var panel = (FrameworkElement)overview.Content;
            panel.Measure(new Size(1212, 664)); panel.Arrange(new Rect(0, 0, 1212, 664)); panel.UpdateLayout();
            Check(((DataGrid)overview.FindName("ServersGrid")).Columns[0].ActualWidth >= 240, "server-name column remains visible during initial layout");
            var rendered = new RenderTargetBitmap(1260, 712, 96, 96, PixelFormats.Pbgra32); rendered.Render(panel);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(rendered));
            using (var file = File.Create(Path.Combine(root, "docker-overview-preview.png"))) png.Save(file);
            overview.Close();
        }
        window.Close();
    }
}
