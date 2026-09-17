using System.Windows;
using RustMonitor.Core;

namespace RustMonitor;

public partial class MainWindow
{
    private readonly Dictionary<string, CombatLogWindow> combatWindows = [];
    private void CombatLog_Click(object sender, RoutedEventArgs e)
    {
        if (profile == null || PlayerList.SelectedItem is not PlayerRow row) return;
        var key = CombatHistory.Key(profile, row.SteamId);
        if (combatWindows.TryGetValue(key, out var existing))
        { if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal; existing.Activate(); return; }
        var window = new CombatLogWindow(store, profile, row.SteamId, row.Name, server?.Name ?? profile.Container) { Owner = this };
        combatWindows.Add(key, window);
        window.Closed += (_, _) => combatWindows.Remove(key);
        window.Show();
    }
}
