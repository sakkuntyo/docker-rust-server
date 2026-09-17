using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using RustMonitor;
using RustMonitor.Core;

internal static partial class Program
{
    private static void LiveInventoryCheck(SshProfile target, string steamId, string wipe)
    {
        var app = new App(); app.InitializeComponent();
        SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(app.Dispatcher));
        using (var db = new Store(Path.Combine(root, "monitor.sqlite3")))
        {
            db.Put("last-ssh-profile", Wire.Write(target));
            db.SaveRoster(target.Key, new ServerState { Protocol = 1, WipeId = wipe, Name = "所持品の直接取得確認" },
                [new PlayerRecord { SteamId = steamId, WipeId = wipe, Name = "確認対象" }]);
            db.SaveInventory(target.Key, new InventorySnapshot { SteamId = steamId, WipeId = wipe, Source = "save", CapturedAt = "2026-01-01T00:00:00Z",
                Items = [new ItemRecord { Uid = "test", Container = "belt", Slot = 1, ItemId = 795236088, Amount = 1 }] });
        }
        var main = new MainWindow(root, serverReader: (_, _) => throw new Exception("Must not read old save"));
        ((ListBox)main.FindName("PlayerList")).SelectedIndex = 0;
        var frame = new System.Windows.Threading.DispatcherFrame();
        var deadline = DateTime.UtcNow.AddSeconds(160);
        var poll = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        var complete = false;
        poll.Tick += (_, _) => { if (DateTime.UtcNow > deadline) frame.Continue = false; };
        app.Dispatcher.BeginInvoke(async () => { await main.RefreshInventoryAsync(); complete = true; frame.Continue = false; });
        poll.Start(); System.Windows.Threading.Dispatcher.PushFrame(frame); poll.Stop();
        using var store = new Store(Path.Combine(root, "monitor.sqlite3"));
        var inventory = store.Inventory(target.Key, wipe, steamId);
        Check(complete && inventory?.Source == "live" && ((TextBox)main.FindName("StatusText")).Text.Contains("現在の所持品を取得しました"),
            "WPF refresh button path reads current possessions through actual SSH and helper, replacing the old save");
        Check(inventory!.Items.All(i => i.Uid != "test"), "old fixture items disappear from the directly fetched inventory");
        Console.WriteLine("Live item count: " + inventory.Items.Count + "; captured: " + inventory.CapturedAt);
        ((Button)main.FindName("InventoryDetailsToggle")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var content = (FrameworkElement)main.Content; content.Measure(new Size(1860, 950)); content.Arrange(new Rect(0, 0, 1860, 950)); content.UpdateLayout();
        SaveRender(content, "inventory-live.png", 1860, 950);
        main.Close();
    }
    private static void InventoryRefreshChecks()
    {
        var fixture = Path.Combine(root, "inventory-refresh");
        var target = new SshProfile("admin@demo.invalid", "rust-demo");
        var state = new ServerState { Protocol = 1, Name = "Demo", WipeId = "save:1789171200:demo" };
        var inventory = new InventorySnapshot { Source = "save", SteamId = CombatSteamId, WipeId = state.WipeId,
            CapturedAt = "2026-09-18T00:00:00Z", Items = [new ItemRecord { Uid = "801", Container = "main", Slot = 0, ItemId = -151838493, Amount = 100 }] };
        var snapshot = new SshServerSnapshot { Server = state, Players = [new PlayerRecord { SteamId = CombatSteamId, Name = "Demo", WipeId = state.WipeId }], Inventories = [inventory] };
        using (var db = new Store(Path.Combine(fixture, "monitor.sqlite3")))
        { db.Put("last-ssh-profile", Wire.Write(target)); db.SaveSshSnapshot(target, snapshot); }
        var pending = new TaskCompletionSource<InventorySnapshot>();
        inventory.Source = "live"; inventory.Current = true; inventory.CapturedAt = "2026-09-18T01:00:00Z"; inventory.MainCapacity = 30;
        var reads = 0;
        using var icons = PrepareRenderIcons(fixture);
        var main = new MainWindow(fixture, serverReader: (_, _) => throw new Exception("Manual refresh must not use the save reader"),
            inventoryReader: (profile, steamId, wipe, _) => { Check(profile == target && steamId == CombatSteamId && wipe == state.WipeId,
                "direct inventory refresh keeps the requested server, player and wipe"); reads++; return pending.Task; }, itemIcons: icons);
        var roster = (ListBox)main.FindName("PlayerList");
        var refresh = (Button)main.FindName("InventoryRefreshButton");
        Check(!refresh.IsEnabled, "inventory refresh is disabled without a player selection");
        roster.SelectedIndex = 0;
        var content = (FrameworkElement)main.Content; content.Measure(new Size(1392, 784)); content.Arrange(new Rect(0, 0, 1392, 784)); content.UpdateLayout();
        var ban = (Button)main.FindName("BanButton");
        var refreshPosition = refresh.TranslatePoint(new Point(), content); var banPosition = ban.TranslatePoint(new Point(), content);
        Check(refresh.IsEnabled && refresh.Content.ToString() == "↻" && refreshPosition.X + refresh.ActualWidth <= banPosition.X &&
            Math.Abs(refreshPosition.Y + refresh.ActualHeight / 2 - banPosition.Y - ban.ActualHeight / 2) < 1,
            "the symbol-only inventory refresh button is directly left of BAN on the same row");
        var running = main.RefreshInventoryAsync(); main.RefreshInventoryAsync().GetAwaiter().GetResult();
        Check(!refresh.IsEnabled && refresh.Content.ToString() == "…" && reads == 1, "inventory refresh disables repeated clicks while pending");
        inventory.Items[0].Amount = 200;
        pending.SetResult(inventory); running.GetAwaiter().GetResult();
        static int Amount(MainWindow view) => ((InventorySlot)((Border)((UniformGrid)((StackPanel)view.FindName("InventoryItems")).Children.OfType<Viewbox>().First().Child).Children[0]).Tag).Item!.Amount;
        Check(Amount(main) == 200 && refresh.IsEnabled && refresh.Content.ToString() == "↻" && !((Button)main.FindName("DisconnectButton")).IsEnabled,
            "manual inventory refresh displays fetched items and keeps automatic updates stopped");
        Check(((TextBox)main.FindName("InventoryStatus")).Text.Contains("直接取得") &&
            ((UniformGrid)((StackPanel)main.FindName("InventoryItems")).Children.OfType<Viewbox>().First().Child).Children.Count == 30,
            "direct inventory is labeled with its capture time and actual container capacity");
        Check(((Border)((UniformGrid)((StackPanel)main.FindName("InventoryItems")).Children.OfType<Viewbox>().First().Child).Children[0]).ContextMenu.Items.OfType<MenuItem>().Single().IsEnabled,
            "directly captured item identity remains usable by the guarded delete action");
        var oldSave = Wire.Read<InventorySnapshot>(Wire.Write(inventory)); oldSave.Source = "save"; oldSave.CapturedAt = "2026-09-18T00:30:00Z"; oldSave.Items[0].Amount = 100;
        using (var db = new Store(Path.Combine(fixture, "monitor.sqlite3")))
        { db.SaveInventory(target.Key, oldSave); Check(db.Inventory(target.Key, state.WipeId, CombatSteamId)?.Items[0].Amount == 200, "older automatic saves cannot overwrite directly fetched inventory"); }
        content.Measure(new Size(1392, 784)); content.Arrange(new Rect(0, 0, 1392, 784)); content.UpdateLayout();
        SaveRender(content, "inventory-refresh.png", 1392, 784);
        pending = new TaskCompletionSource<InventorySnapshot>(); running = main.RefreshInventoryAsync();
        pending.SetException(new IOException("通信失敗")); running.GetAwaiter().GetResult();
        Check(Amount(main) == 200 && refresh.IsEnabled && ((TextBox)main.FindName("StatusText")).Text.Contains("更新できません"),
            "a failed inventory refresh retains previous items and allows another attempt");
        pending = new TaskCompletionSource<InventorySnapshot>(); running = main.RefreshInventoryAsync();
        pending.SetException(new IOException("本人の身体が見つかりません")); running.GetAwaiter().GetResult();
        Check(Amount(main) == 200 && ((TextBox)main.FindName("StatusText")).Text.Contains("見つかりません"),
            "missing inventory is reported without replacing the previous list with empty slots");
        pending = new TaskCompletionSource<InventorySnapshot>(); running = main.RefreshInventoryAsync();
        roster.SelectedIndex = -1; inventory.Items[0].Amount = 300;
        pending.SetResult(inventory); running.GetAwaiter().GetResult(); roster.SelectedIndex = 0;
        Check(Amount(main) == 200, "a response arriving after player selection changed does not replace the displayed inventory");
        pending = new TaskCompletionSource<InventorySnapshot>(); running = main.RefreshInventoryAsync();
        ((Button)main.FindName("DisconnectButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        pending.SetResult(inventory); running.GetAwaiter().GetResult();
        Check(Amount(main) == 200 && refresh.IsEnabled, "disconnecting discards an in-flight inventory refresh response");
        pending = new TaskCompletionSource<InventorySnapshot>(); running = main.RefreshInventoryAsync();
        var empty = Wire.Read<InventorySnapshot>(Wire.Write(inventory)); empty.Items.Clear();
        pending.SetResult(empty); running.GetAwaiter().GetResult();
        Check(((UniformGrid)((StackPanel)main.FindName("InventoryItems")).Children.OfType<Viewbox>().First().Child).Children.Cast<Border>().All(c => ((InventorySlot)c.Tag).Item == null),
            "an available but empty live inventory clears the old item list");
        oldSave.CapturedAt = "2026-09-18T02:00:00Z";
        using (var db = new Store(Path.Combine(fixture, "monitor.sqlite3")))
        { db.SaveInventory(target.Key, oldSave); Check(db.Inventory(target.Key, state.WipeId, CombatSteamId)?.Source == "save", "a genuinely newer save can supersede an older direct observation"); }
        main.Close();
    }
}
