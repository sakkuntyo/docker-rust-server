using System.IO;
using System.Windows;
using RustMonitor.Core;

namespace RustMonitor;

public partial class MainWindow
{
    private bool refreshingInventory;
    private void UpdateInventoryRefreshButton()
    {
        InventoryRefreshButton.IsEnabled = !closed && !serverBusy && !refreshingInventory && !refreshingDelete &&
            profile != null && PlayerList.SelectedItem is PlayerRow;
        InventoryRefreshButton.Content = refreshingInventory ? "…" : "↻";
    }
    private async void InventoryRefresh_Click(object sender, RoutedEventArgs e) => await RefreshInventoryAsync();
    public async Task RefreshInventoryAsync()
    {
        if (closed || serverBusy || refreshingInventory || refreshingDelete || moderationWindow != null ||
            profile == null || PlayerList.SelectedItem is not PlayerRow row) return;
        var target = profile;
        var current = generation;
        var token = session.Token;
        var held = false;
        refreshingInventory = true; UpdateInventoryRefreshButton();
        Status(row.Name + " • 所持品を更新しています…");
        bool StillSelected() => !closed && current == generation && profile == target &&
            PlayerList.SelectedItem is PlayerRow selected && selected.SteamId == row.SteamId;
        try
        {
            await syncGate.WaitAsync(token); held = true;
            if (!StillSelected()) return;
            var snapshot = await readServer(target, token);
            if (!StillSelected()) return;
            var inventory = snapshot.Inventories.SingleOrDefault(i => i.SteamId == row.SteamId && i.WipeId == snapshot.Server.WipeId);
            if (inventory == null) throw new InvalidDataException("最新セーブに所持品が見つかりません。前回の表示を保持しています。");
            store.SaveSshSnapshot(target, snapshot);
            server = snapshot.Server; players = store.Players(target.Key); presenceAvailable = snapshot.PresenceAvailable;
            BindRoster(); LoadMap();
            if (!StillSelected()) return;
            ShowInventory(inventory);
            Status(row.Name + " • 所持品を更新しました（セーブ " + Time(inventory.CapturedAt) + "）");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { if (StillSelected()) Status("所持品を更新できませんでした • " + SafeError(ex)); }
        finally
        {
            if (held) syncGate.Release();
            refreshingInventory = false;
            if (!closed) UpdateInventoryRefreshButton();
        }
    }
}
