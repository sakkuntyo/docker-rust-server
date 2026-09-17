using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using RustMonitor;
using RustMonitor.Core;

internal static partial class Program
{
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
        var pending = new TaskCompletionSource<SshServerSnapshot>();
        var reads = 0;
        using var icons = PrepareRenderIcons(fixture);
        var main = new MainWindow(fixture, serverReader: (profile, _) => { Check(profile == target, "inventory refresh keeps the requested server"); reads++; return pending.Task; }, itemIcons: icons);
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
        pending.SetResult(snapshot); running.GetAwaiter().GetResult();
        static int Amount(MainWindow view) => ((InventorySlot)((Border)((UniformGrid)((StackPanel)view.FindName("InventoryItems")).Children.OfType<Viewbox>().First().Child).Children[0]).Tag).Item!.Amount;
        Check(Amount(main) == 200 && refresh.IsEnabled && refresh.Content.ToString() == "↻" && !((Button)main.FindName("DisconnectButton")).IsEnabled,
            "manual inventory refresh displays fetched items and keeps automatic updates stopped");
        content.Measure(new Size(1392, 784)); content.Arrange(new Rect(0, 0, 1392, 784)); content.UpdateLayout();
        SaveRender(content, "inventory-refresh.png", 1392, 784);
        pending = new TaskCompletionSource<SshServerSnapshot>(); running = main.RefreshInventoryAsync();
        pending.SetException(new IOException("通信失敗")); running.GetAwaiter().GetResult();
        Check(Amount(main) == 200 && refresh.IsEnabled && ((TextBox)main.FindName("StatusText")).Text.Contains("更新できません"),
            "a failed inventory refresh retains previous items and allows another attempt");
        pending = new TaskCompletionSource<SshServerSnapshot>(); running = main.RefreshInventoryAsync();
        pending.SetResult(new SshServerSnapshot { Server = state, Players = snapshot.Players }); running.GetAwaiter().GetResult();
        Check(Amount(main) == 200 && ((TextBox)main.FindName("StatusText")).Text.Contains("見つかりません"),
            "missing inventory is reported without replacing the previous list with empty slots");
        pending = new TaskCompletionSource<SshServerSnapshot>(); running = main.RefreshInventoryAsync();
        roster.SelectedIndex = -1; inventory.Items[0].Amount = 300;
        pending.SetResult(snapshot); running.GetAwaiter().GetResult(); roster.SelectedIndex = 0;
        Check(Amount(main) == 200, "a response arriving after player selection changed does not replace the displayed inventory");
        pending = new TaskCompletionSource<SshServerSnapshot>(); running = main.RefreshInventoryAsync();
        ((Button)main.FindName("DisconnectButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        pending.SetResult(snapshot); running.GetAwaiter().GetResult();
        Check(Amount(main) == 200 && refresh.IsEnabled, "disconnecting discards an in-flight inventory refresh response");
        main.Close();
    }
}
