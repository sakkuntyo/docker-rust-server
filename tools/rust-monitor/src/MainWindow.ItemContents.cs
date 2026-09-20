using System.Windows;
using System.Windows.Controls;
using RustMonitor.Core;

namespace RustMonitor;

public partial class MainWindow
{
    private readonly Dictionary<string, ItemContentsWindow> contentsWindows = [];
    private void AddOpenContentsMenu(Border cell, InventorySlot slot)
    {
        if (profile == null || shownInventory == null || PlayerList.SelectedItem is not PlayerRow row || shownInventory.SteamId != row.SteamId || slot.Item is not { } item || !InventoryLayout.CanOpen(item)) return;
        var target = profile; var snapshot = shownInventory; var title = server?.Name ?? target.Container;
        var menu = cell.ContextMenu ?? new ContextMenu { Style = (Style)FindResource("HistoryMenuStyle"), MinWidth = 220 };
        var open = new MenuItem { Header = "開く", Style = (Style)FindResource("HistoryItemStyle") };
        open.Click += (_, _) => OpenItemContents(target, row.Name, title, snapshot, item);
        menu.Items.Insert(0, open); cell.ContextMenu = menu;
    }
    private void OpenItemContents(SshProfile target, string player, string title, InventorySnapshot snapshot, ItemRecord item)
    {
        if (closed) return;
        var identity = item.Uid.Length > 0 ? item.Uid : item.Container + ":" + item.Slot + ":" + item.ItemId;
        var key = target.Key + ":" + snapshot.WipeId + ":" + snapshot.SteamId + ":" + identity;
        if (contentsWindows.TryGetValue(key, out var existing))
        { if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal; existing.Activate(); return; }
        var window = new ItemContentsWindow(target, player, title, snapshot, item, icons, readInventory) { Owner = this };
        contentsWindows.Add(key, window); window.Closed += (_, _) => contentsWindows.Remove(key);
        window.OpenRequested += (child, current) => OpenItemContents(target, player, title, current, child);
        window.DeleteRequested += (action, captured) => OpenModeration(target, action, title, captured);
        window.Show();
    }
}
