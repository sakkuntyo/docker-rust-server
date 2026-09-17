using System.Windows;
using RustMonitor.Core;

namespace RustMonitor;

public partial class MainWindow
{
    private readonly Dictionary<string, ItemGiveWindow> itemWindows = [];
    private void GiveItem_Click(object sender, RoutedEventArgs e)
    {
        if (profile == null || PlayerList.SelectedItem is not PlayerRow row) return;
        var key = profile.Key + ":" + row.SteamId;
        if (itemWindows.TryGetValue(key, out var existing))
        { if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal; existing.Activate(); return; }
        var window = new ItemGiveWindow(store, profile, row.SteamId, row.Name, server?.Name ?? profile.Container, icons) { Owner = this };
        itemWindows.Add(key, window); window.Closed += (_, _) => itemWindows.Remove(key); window.Show();
    }
}
