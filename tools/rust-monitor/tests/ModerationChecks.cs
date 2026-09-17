using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using RustMonitor;
using RustMonitor.Core;

internal static partial class Program
{
    private static void ModerationChecks()
    {
        var profile = new SshProfile("admin@demo.invalid", "rust-demo");
        var item = new ItemRecord { Uid = "18446744073709551614", ItemId = -151838493, Name = "Wood", ShortName = "wood", Amount = 1000, Slot = 0, Container = "main" };
        var request = new ModerationRequest { Action = "delete", SteamId = CombatSteamId, Name = "Demo Player", WipeId = "save:1789171200:demo", Item = item };
        request.Validate();
        Check(Wire.Read<ModerationRequest>(Wire.Write(request)).Item!.Uid == item.Uid, "deletion identity preserves full unsigned 64-bit item UIDs");
        var legacy = Wire.Read<ModerationRequest>(Wire.Write(request)); legacy.Item!.Uid = "";
        try { legacy.Validate(); Check(false, "legacy delete"); } catch (ArgumentException) { Check(true, "historical items without instance identity cannot be deleted"); }
        var calls = new List<(SshProfile, ModerationRequest)>();
        var pending = new TaskCompletionSource<ModerationResult>();
        var window = new ModerationWindow(profile, request, "DEMO / Monthly", "2026-09-18T00:00:00Z",
            (p, r, _) => { calls.Add((p, r)); return pending.Task; });
        Check(calls.Count == 0, "opening deletion confirmation performs no server writes");
        request.SteamId = "76561198000000002"; item.Amount = 1;
        var task = window.ExecuteAsync(); window.ExecuteAsync().GetAwaiter().GetResult(); window.Close();
        Check(window.IsSending && calls.Count == 1 && calls[0].Item1 == profile && calls[0].Item2.SteamId == CombatSteamId && calls[0].Item2.Item!.Amount == 1000,
            "delete captures the original player and stack, blocks duplicate clicks and closing while pending");
        pending.SetResult(new ModerationResult { State = "unknown", Message = "結果不明・自動再送しません" }); task.GetAwaiter().GetResult();
        window.ExecuteAsync().GetAwaiter().GetResult();
        Check(!window.IsSending && calls.Count == 1 && !((Button)window.FindName("ExecuteButton")).IsEnabled, "an uncertain deletion cannot be resubmitted from its confirmation window");
        var content = (FrameworkElement)window.Content; content.Measure(new Size(570, 500)); content.Arrange(new Rect(0, 0, 570, 500)); content.UpdateLayout();
        SaveRender(content, "delete-confirmation.png", 570, 500); window.Close();
        var ban = new ModerationWindow(profile, new ModerationRequest { Action = "ban", SteamId = CombatSteamId, Name = "Demo Player" }, "DEMO / Monthly", send:
            (_, r, _) => { calls.Add((profile, r)); return Task.FromResult(new ModerationResult { State = "accepted", Message = "BANしました" }); });
        var reason = (TextBox)ban.FindName("ReasonBox"); reason.Text = "\n"; ban.ExecuteAsync().GetAwaiter().GetResult();
        Check(calls.Count == 1 && !ban.IsSending, "empty BAN reasons do not reach the server");
        reason.Text = "テスト理由";
        content = (FrameworkElement)ban.Content; content.Measure(new Size(570, 500)); content.Arrange(new Rect(0, 0, 570, 500)); content.UpdateLayout();
        SaveRender(content, "ban-confirmation.png", 570, 500);
        ban.ExecuteAsync().GetAwaiter().GetResult();
        Check(calls.Count == 2 && calls[1].Item2.Action == "ban" && calls[1].Item2.Reason == "テスト理由" && ban.Result?.State == "accepted", "explicit BAN sends the fixed Steam ID and entered reason");
        ban.Close();

        var fixture = Path.Combine(root, "moderation-ui"); Directory.CreateDirectory(fixture);
        var freshItem = new ItemRecord { Uid = "777", Container = "main", Slot = 1, ItemId = 1, Amount = 1 };
        var freshInventory = new InventorySnapshot { SteamId = CombatSteamId, WipeId = request.WipeId, Source = "save", CapturedAt = "2026-09-18T01:00:00Z", Items = [item, freshItem] };
        var snapshot = new SshServerSnapshot { Server = new ServerState { Protocol = 1, WipeId = request.WipeId, Name = "Demo" },
            Players = [new PlayerRecord { SteamId = CombatSteamId, Name = "Demo Player", WipeId = request.WipeId }], Inventories = [freshInventory] };
        var refreshing = new TaskCompletionSource<SshServerSnapshot>();
        var reads = 0;
        using (var db = new Store(Path.Combine(fixture, "monitor.sqlite3")))
        {
            db.Put("last-ssh-profile", Wire.Write(profile));
            db.SaveRoster(profile.Key, new ServerState { WipeId = request.WipeId, Name = "Demo" }, [new PlayerRecord { SteamId = CombatSteamId, Name = "Demo Player" }]);
            db.SaveInventory(profile.Key, new InventorySnapshot { SteamId = CombatSteamId, WipeId = request.WipeId, Source = "save", CapturedAt = "2026-09-18T00:00:00Z", Items = [item, new ItemRecord { Slot = 1, Container = "main", ItemId = 1, Amount = 1 }] });
        }
        using var icons = PrepareRenderIcons(fixture);
        var main = new MainWindow(fixture, serverReader: (_, _) => { reads++; return refreshing.Task; }, itemIcons: icons);
        ((ListBox)main.FindName("PlayerList")).SelectedIndex = 0;
        var slots = (UniformGrid)((StackPanel)main.FindName("InventoryItems")).Children.OfType<Viewbox>().First().Child;
        Check(((Border)slots.Children[0]).ContextMenu.Items.OfType<MenuItem>().Single().IsEnabled &&
            ((Border)slots.Children[1]).ContextMenu.Items.OfType<MenuItem>().Single().IsEnabled &&
            ((Border)slots.Children[1]).ContextMenu.Items.OfType<MenuItem>().Single().Header.ToString()!.Contains("更新して確認") && ((Border)slots.Children[2]).ContextMenu == null,
            "legacy inventory offers refresh-before-confirm instead of an unusable disabled deletion menu");
        content = (FrameworkElement)main.Content; content.Measure(new Size(1392, 784)); content.Arrange(new Rect(0, 0, 1392, 784)); content.UpdateLayout();
        var banButton = (Button)main.FindName("BanButton"); var giveButton = (Button)main.FindName("GiveItemButton");
        var pos = banButton.TranslatePoint(new Point(), content); var givePos = giveButton.TranslatePoint(new Point(), content);
        Check(pos.X + banButton.ActualWidth <= givePos.X && Math.Abs(pos.Y - givePos.Y) < 1 && ((SolidColorBrush)banButton.Background).Color.R > ((SolidColorBrush)banButton.Background).Color.G,
            "the red BAN button is immediately left of add-item without an extra row");
        SaveRender(content, "moderation-main.png", 1392, 784);
        var legacyAction = new ModerationRequest { Action = "delete", SteamId = CombatSteamId, Name = "Demo Player", WipeId = request.WipeId,
            Item = new ItemRecord { ItemId = 1, Amount = 1, Slot = 1, Container = "main" } };
        var preparation = main.RefreshDeletionAsync(profile, legacyAction);
        Check(main.RefreshDeletionAsync(profile, legacyAction).GetAwaiter().GetResult() == null && reads == 1,
            "repeated legacy deletion clicks cannot start overlapping reads or mutations");
        refreshing.SetResult(snapshot);
        var prepared = preparation.GetAwaiter().GetResult();
        Check(prepared?.Action.Item?.Uid == "777" && prepared?.CapturedAt == freshInventory.CapturedAt && reads == 1 && calls.Count == 2,
            "read-only refresh supplies identity to a separate confirmation without deleting an item");
        freshItem.Amount = 2;
        try { legacyAction.WithFreshIdentity(freshInventory); Check(false, "changed legacy item"); }
        catch (ArgumentException) { Check(true, "changed cached stacks require selecting the updated item again"); }
        freshItem.Amount = 1;
        freshInventory.WipeId = "save:1789171200:other";
        try { legacyAction.WithFreshIdentity(freshInventory); Check(false, "changed wipe"); }
        catch (ArgumentException) { Check(true, "refresh cannot substitute an item from another wipe"); }
        freshInventory.WipeId = request.WipeId;
        refreshing = new TaskCompletionSource<SshServerSnapshot>();
        var stale = main.RefreshDeletionAsync(profile, legacyAction);
        ((ListBox)main.FindName("PlayerList")).SelectedIndex = -1;
        refreshing.SetResult(snapshot);
        Check(stale.GetAwaiter().GetResult() == null, "changing player selection during refresh cancels the pending confirmation");
        main.Close();
    }
}
