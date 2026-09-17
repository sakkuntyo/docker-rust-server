using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RustMonitor.Core;

namespace RustMonitor;

public partial class MainWindow
{
    private InventorySnapshot? shownInventory;
    private ModerationWindow? moderationWindow;
    private void Ban_Click(object sender, RoutedEventArgs e)
    {
        if (profile == null || PlayerList.SelectedItem is not PlayerRow row) return;
        OpenModeration(profile, new ModerationRequest { Action = "ban", SteamId = row.SteamId, Name = row.Name }, server?.Name ?? profile.Container, "");
    }
    private static string DeletedKey(SshProfile target, ModerationRequest action) =>
        "deleted-item:" + target.Key + ":" + action.WipeId + ":" + action.SteamId + ":" + action.Item?.Uid;
    private void AddDeleteMenu(Border cell, InventorySlot slot)
    {
        if (profile == null || shownInventory == null || PlayerList.SelectedItem is not PlayerRow row || slot.Item == null) return;
        var target = profile;
        var title = server?.Name ?? target.Container;
        var captured = shownInventory.CapturedAt;
        var action = new ModerationRequest { Action = "delete", SteamId = row.SteamId, Name = row.Name, WipeId = shownInventory.WipeId,
            Item = Wire.Read<ItemRecord>(Wire.Write(slot.Item)) };
        var valid = slot.PositionKnown && shownInventory.Source == "save" && shownInventory.SteamId == row.SteamId;
        try { action.Validate(); } catch (ArgumentException) { valid = false; }
        var deleted = store.Get(DeletedKey(target, action)) == "true";
        if (deleted) { cell.Opacity = .4; cell.ToolTip = "削除済み • 次のサーバー保存後に一覧へ反映されます。"; }
        var menu = new ContextMenu { Style = (Style)FindResource("HistoryMenuStyle"), MinWidth = 220 };
        var remove = new MenuItem { Header = deleted ? "削除済み（セーブ反映待ち）" : valid ? "削除" : "削除（所持品の更新が必要）", IsEnabled = valid && !deleted,
            Foreground = new SolidColorBrush(Color.FromRgb(255, 150, 150)), Style = (Style)FindResource("HistoryItemStyle") };
        if (!valid) remove.ToolTip = "位置・識別情報が不足しています。SSHで所持品を更新してください。";
        remove.Click += (_, _) => OpenModeration(target, action, title, captured);
        menu.Items.Add(remove); cell.ContextMenu = menu;
    }
    private void OpenModeration(SshProfile target, ModerationRequest action, string title, string captured)
    {
        if (moderationWindow != null) { moderationWindow.Activate(); return; }
        var window = new ModerationWindow(target, action, title, captured) { Owner = this };
        moderationWindow = window;
        try
        {
            window.ShowDialog();
            if (closed) return;
            if (window.Result?.State == "accepted" && action.Action == "delete")
            {
                store.Put(DeletedKey(target, action), "true");
                ShowSelectedInventory();
            }
            if (window.Result != null) Status(action.Name + " • " + window.Result.Message);
        }
        finally { moderationWindow = null; }
    }
}
