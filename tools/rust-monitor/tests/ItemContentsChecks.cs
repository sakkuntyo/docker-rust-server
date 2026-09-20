using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using RustMonitor;
using RustMonitor.Core;

internal static partial class Program
{
    private static void ItemContentsChecks()
    {
        var target = new SshProfile("admin@demo.invalid", "rust-demo");
        var bag = new ItemRecord { Uid = "100", ItemId = -907422733, ShortName = "largebackpack", Container = "wear", Slot = 7, Amount = 1,
            Contents = [new ItemRecord { Uid = "101", ItemId = -151838493, ShortName = "wood", Amount = 500, Container = "contents", Slot = 1 },
                new ItemRecord { Uid = "102", ItemId = 1545779598, ShortName = "rifle.ak", Amount = 1, Container = "wear", Slot = 7,
                    Contents = [new ItemRecord { Uid = "103", ItemId = 567235583, ShortName = "weapon.mod.8x.scope", Amount = 1, Slot = 0 }] }] };
        var snapshot = new InventorySnapshot { SteamId = CombatSteamId, WipeId = "save:1789171200:demo", Source = "save", CapturedAt = "2026-09-20T00:00:00Z", Items = [bag] };
        Check(InventoryLayout.CanOpen(bag) && InventoryLayout.CanOpen(new ItemRecord { ItemId = -907422733 }) && !InventoryLayout.CanOpen(bag.Contents[0]),
            "backpacks can open when empty or populated and ordinary items without contents are unchanged");
        var slots = InventoryLayout.ContentSlots(bag);
        Check(slots.Count == 8 && slots[0].Item == null && slots[1].Item?.Uid == "101" && slots[7].Item?.Uid == "102",
            "contents layout keeps slot gaps and includes both saved and live container labels");
        var fixture = Path.Combine(root, "contents-ui");
        using var icons = PrepareRenderIcons(fixture);
        var pending = new TaskCompletionSource<InventorySnapshot>(); var reads = 0;
        var window = new ItemContentsWindow(target, "Demo Player", "Demo server", snapshot, bag, icons, (profile, steam, wipe, _) =>
        { Check(profile == target && steam == CombatSteamId && wipe == snapshot.WipeId, "contents refresh keeps its original server, player and wipe"); reads++; return pending.Task; });
        var grid = (UniformGrid)window.FindName("ContentsGrid");
        Check(grid.Children.Count == 8 && ((TextBox)window.FindName("CountText")).Text == "2 個", "contents window renders item count and original slots");
        var opened = "";
        window.OpenRequested += (item, _) => opened = item.Uid;
        ((Border)grid.Children[7]).ContextMenu.Items.OfType<MenuItem>().First().RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Check(opened == "102" && ((Border)grid.Children[1]).ContextMenu.Items.OfType<MenuItem>().Single().Header.ToString() == "削除", "nested contents offer Open and ordinary contents offer Delete");
        ModerationRequest? deletion = null;
        window.DeleteRequested += (request, _) => deletion = request;
        ((Border)grid.Children[1]).ContextMenu.Items.OfType<MenuItem>().Single().RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Check(deletion?.Item?.Uid == "101" && deletion.Parents.Single().Uid == "100" && deletion.Item.Container == "wear" && reads == 0,
            "right-click Delete prepares the selected child and parent chain without sending a mutation");
        var attachment = ModerationRequest.DeleteItem(snapshot, bag.Contents[1].Contents[0], "Demo");
        Check(attachment.Parents.Select(p => p.Uid).SequenceEqual(new[] { "100", "102" }), "nested deletion records every ancestor from the player's root container");
        var content = (FrameworkElement)window.Content; content.Measure(new Size(626, 610)); content.Arrange(new Rect(0, 0, 626, 610)); content.UpdateLayout();
        SaveRender(content, "item-contents-preview.png", 626, 610);
        var refreshing = window.RefreshAsync(); window.RefreshAsync().GetAwaiter().GetResult();
        Check(reads == 1 && !((Button)window.FindName("RefreshButton")).IsEnabled, "contents refresh does not overlap or send item mutations");
        var fresh = Wire.Read<InventorySnapshot>(Wire.Write(snapshot)); fresh.Source = "live"; fresh.CapturedAt = "2026-09-20T00:01:00Z";
        fresh.Items[0].Container = "main"; fresh.Items[0].Slot = 0; fresh.Items[0].Contents[0].Amount = 600;
        pending.SetResult(fresh); refreshing.GetAwaiter().GetResult();
        Check(((InventorySlot)((Border)grid.Children[1]).Tag).Item?.Amount == 600 && ((TextBox)window.FindName("SnapshotText")).Text.StartsWith("直接取得"),
            "refresh follows backpack identity even when it moves to another container or slot");
        pending = new TaskCompletionSource<InventorySnapshot>(); refreshing = window.RefreshAsync();
        pending.SetException(new IOException("Test connection failure")); refreshing.GetAwaiter().GetResult();
        Check(grid.Children.Count == 8 && ((TextBox)window.FindName("StatusText")).Text.Contains("前回"), "failed contents refresh preserves the previous display and capture time");
        pending = new TaskCompletionSource<InventorySnapshot>(); refreshing = window.RefreshAsync();
        var wrongWipe = Wire.Read<InventorySnapshot>(Wire.Write(fresh)); wrongWipe.WipeId = "save:2:other";
        pending.SetResult(wrongWipe); refreshing.GetAwaiter().GetResult();
        Check(grid.Children.Count == 8, "contents responses for another wipe cannot replace the displayed bag");
        pending = new TaskCompletionSource<InventorySnapshot>();
        window.ApplyDeletion(target, deletion!);
        pending.SetResult(fresh);
        Check(((InventorySlot)((Border)grid.Children[1]).Tag).Item == null && ((TextBox)window.FindName("CountText")).Text == "1 個",
            "successful child deletion immediately updates contents and stale refresh data cannot resurrect it");
        pending = new TaskCompletionSource<InventorySnapshot>(); refreshing = window.RefreshAsync();
        fresh.Items[0].Uid = "999"; pending.SetResult(fresh); refreshing.GetAwaiter().GetResult();
        Check(grid.Children.Count == 0 && ((TextBlock)window.FindName("EmptyText")).Text.Contains("ありません"), "a different backpack in the old slot never replaces the original target");
        pending = new TaskCompletionSource<InventorySnapshot>(); refreshing = window.RefreshAsync();
        fresh.Items[0].Uid = "100"; fresh.Items[0].Contents.Clear(); pending.SetResult(fresh); refreshing.GetAwaiter().GetResult();
        Check(((TextBlock)window.FindName("EmptyText")).Text == "中身は空です" && ((TextBox)window.FindName("CountText")).Text == "0 個", "an empty backpack is distinct from a missing backpack");
        pending = new TaskCompletionSource<InventorySnapshot>(); refreshing = window.RefreshAsync();
        window.Close(); pending.SetResult(fresh); refreshing.GetAwaiter().GetResult();
        Check(grid.Children.Count == 0, "closing during refresh ignores late results");

        using (var db = new Store(Path.Combine(fixture, "monitor.sqlite3")))
        { db.Put("last-ssh-profile", Wire.Write(target)); db.SaveRoster(target.Key, new ServerState { WipeId = snapshot.WipeId }, [new PlayerRecord { SteamId = CombatSteamId, Name = "Demo" }]); db.SaveInventory(target.Key, snapshot); }
        var main = new MainWindow(fixture, itemIcons: icons);
        ((ListBox)main.FindName("PlayerList")).SelectedIndex = 0;
        var wear = (UniformGrid)((StackPanel)main.FindName("InventoryItems")).Children.OfType<Viewbox>().Last().Child;
        Check(((Border)wear.Children[7]).ContextMenu.Items.OfType<MenuItem>().Select(i => i.Header.ToString()).SequenceEqual(new[] { "開く", "削除" }),
            "main inventory right-click offers Open above the existing delete action for backpacks");
        main.ApplyModerationResult(target, deletion!, new ModerationResult { State = "accepted" });
        wear = (UniformGrid)((StackPanel)main.FindName("InventoryItems")).Children.OfType<Viewbox>().Last().Child;
        Check(((InventorySlot)((Border)wear.Children[7]).Tag).Item!.Contents.All(i => i.Uid != "101"), "main inventory excludes deleted children from the backpack tooltip and future contents windows");
        main.Close();
    }
    private static void BackpackPreview(string file)
    {
        var app = new App(); app.InitializeComponent();
        SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(app.Dispatcher));
        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        var snapshot = Wire.Read<InventorySnapshot>(doc.RootElement.GetProperty("Inventory").GetRawText());
        var bag = snapshot.Items.First(i => InventoryLayout.CanOpen(i) && i.Contents.Count > 0);
        using var icons = new ItemIcons(Path.Combine(root, "icons"));
        var window = new ItemContentsWindow(new SshProfile("admin@demo.invalid", "rust-demo"), "確認用プレイヤー", "Demo server", snapshot, bag, icons,
            (_, _, _, _) => Task.FromResult(snapshot));
        window.RefreshAsync().GetAwaiter().GetResult();
        var content = (FrameworkElement)window.Content; content.Measure(new Size(626, 700)); content.Arrange(new Rect(0, 0, 626, 700)); content.UpdateLayout();
        var images = VisualChildren<Image>(content).ToArray();
        var frame = new System.Windows.Threading.DispatcherFrame(); var deadline = DateTime.UtcNow.AddSeconds(20);
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += (_, _) => { if (images.All(i => i.Source != null) || DateTime.UtcNow > deadline) frame.Continue = false; };
        timer.Start(); System.Windows.Threading.Dispatcher.PushFrame(frame); timer.Stop();
        Check(images.Length == bag.Contents.Count && images.Count(i => i.Source != null) > 0, "real backpack contents render with item icons and quantities");
        content.UpdateLayout(); SaveRender(content, "backpack-live-preview.png", 626, 700);
        window.Close();
    }
}
