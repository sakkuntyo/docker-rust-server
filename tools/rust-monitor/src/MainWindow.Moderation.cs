using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RustMonitor.Core;

namespace RustMonitor;

public partial class MainWindow
{
    private InventorySnapshot? shownInventory;
    private ModerationWindow? moderationWindow;
    private bool refreshingDelete;
    private void Ban_Click(object sender, RoutedEventArgs e)
    {
        if (refreshingDelete || profile == null || PlayerList.SelectedItem is not PlayerRow row) return;
        OpenModeration(profile, new ModerationRequest { Action = "ban", SteamId = row.SteamId, Name = row.Name }, server?.Name ?? profile.Container, "");
    }
    private static string DeletedKey(SshProfile target, ModerationRequest action) =>
        DeletedKey(target, action.WipeId, action.SteamId, action.Item?.Uid ?? "");
    private static string DeletedKey(SshProfile target, string wipe, string steamId, string uid) =>
        "deleted-item:" + target.Key + ":" + wipe + ":" + steamId + ":" + uid;
    private bool WasDeleted(InventorySnapshot snapshot, ItemRecord item) => profile != null &&
        ulong.TryParse(item.Uid, out var uid) && uid > 0 &&
        store.Get(DeletedKey(profile, snapshot.WipeId, snapshot.SteamId, item.Uid)) == "true";
    private void AddDeleteMenu(Border cell, InventorySlot slot)
    {
        if (profile == null || shownInventory == null || PlayerList.SelectedItem is not PlayerRow row || slot.Item == null) return;
        var target = profile;
        var title = server?.Name ?? target.Container;
        var captured = shownInventory.CapturedAt;
        var action = new ModerationRequest { Action = "delete", SteamId = row.SteamId, Name = row.Name, WipeId = shownInventory.WipeId,
            Item = Wire.Read<ItemRecord>(Wire.Write(slot.Item)) };
        var available = slot.PositionKnown && shownInventory.Source is "save" or "live" && shownInventory.SteamId == row.SteamId;
        var valid = available;
        try { action.Validate(); } catch (ArgumentException) { valid = false; }
        var deleted = store.Get(DeletedKey(target, action)) == "true";
        if (deleted) { cell.Opacity = .4; cell.ToolTip = "削除済み • 次のサーバー保存後に一覧へ反映されます。"; }
        var menu = new ContextMenu { Style = (Style)FindResource("HistoryMenuStyle"), MinWidth = 220 };
        var remove = new MenuItem { Header = deleted ? "削除済み（セーブ反映待ち）" : valid ? "削除" : available ? "削除（更新して確認）" : "削除（位置が未確認）", IsEnabled = available && !deleted,
            Foreground = new SolidColorBrush(Color.FromRgb(255, 150, 150)), Style = (Style)FindResource("HistoryItemStyle") };
        if (!valid) remove.ToolTip = "サーバーから識別情報を再取得して確認画面を開きます。この段階では削除しません。";
        remove.Click += async (_, _) =>
        {
            if (valid) OpenModeration(target, action, title, captured);
            else
            {
                var fresh = await RefreshDeletionAsync(target, action);
                if (fresh != null) OpenModeration(target, fresh.Value.Action, title, fresh.Value.CapturedAt);
            }
        };
        menu.Items.Add(remove); cell.ContextMenu = menu;
    }
    public async Task<(ModerationRequest Action, string CapturedAt)?> RefreshDeletionAsync(SshProfile target, ModerationRequest action)
    {
        if (refreshingDelete || refreshingInventory || moderationWindow != null || closed || target != profile || PlayerList.SelectedItem is not PlayerRow row || row.SteamId != action.SteamId) return null;
        refreshingDelete = true;
        UpdateInventoryRefreshButton();
        var current = generation;
        bool held = false;
        Status("削除対象の識別情報をSSHで更新しています…（まだ削除しません）");
        try
        {
            await syncGate.WaitAsync(session.Token); held = true;
            var snapshot = await readServer(target, session.Token);
            if (closed || current != generation || profile != target || PlayerList.SelectedItem is not PlayerRow selected || selected.SteamId != action.SteamId) return null;
            var inventory = snapshot.Inventories.SingleOrDefault(i => i.SteamId == action.SteamId && i.WipeId == snapshot.Server.WipeId);
            if (inventory == null) throw new InvalidDataException("このプレイヤーの所持品を再取得できません。削除していません。");
            store.SaveSshSnapshot(target, snapshot);
            server = snapshot.Server; players = store.Players(target.Key); presenceAvailable = snapshot.PresenceAvailable;
            BindRoster(); LoadMap();
            // Another app version may write the shared cache. Use this fresh response
            // directly for the confirmation instead of re-reading that cache.
            ShowInventory(inventory);
            var fresh = action.WithFreshIdentity(inventory);
            Status("所持品を更新しました。削除する対象を確認してください。");
            return (fresh, inventory.CapturedAt);
        }
        catch (Exception ex) { if (!closed && current == generation) Status("削除していません • " + SafeError(ex)); return null; }
        finally { if (held) syncGate.Release(); refreshingDelete = false; if (!closed) UpdateInventoryRefreshButton(); }
    }
    private void OpenModeration(SshProfile target, ModerationRequest action, string title, string captured)
    {
        if (closed || refreshingDelete) return;
        if (moderationWindow != null) { moderationWindow.Activate(); return; }
        var window = new ModerationWindow(target, action, title, captured) { Owner = this };
        window.Completed += result => ApplyModerationResult(target, action, result);
        moderationWindow = window;
        try { window.ShowDialog(); }
        finally { moderationWindow = null; }
    }
    public void ApplyModerationResult(SshProfile target, ModerationRequest action, ModerationResult result)
    {
        if (closed) return;
        if (result.State == "accepted" && action.Action == "delete")
        {
            try
            {
                action.Validate();
                store.Put(DeletedKey(target, action), "true");
                foreach (var window in contentsWindows.Values.ToArray()) window.ApplyDeletion(target, action);
                ShowSelectedInventory();
                result.Message = "対象のアイテム（スタック全体・内容物を含む）を削除し、一覧に反映しました。";
            }
            catch (Exception)
            { result.Message = "アイテムの削除は成功しましたが、一覧の更新に失敗しました。削除を再実行せず、一覧を更新してください。"; }
        }
        Status(action.Name + " • " + result.Message);
    }
}
