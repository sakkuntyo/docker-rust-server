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
        var wipe = server?.WipeId ?? "";
        var current = generation;
        var token = session.Token;
        var held = false;
        refreshingInventory = true; UpdateInventoryRefreshButton();
        Status(row.Name + " • サーバーの現在の所持品を取得しています…");
        bool StillSelected() => !closed && current == generation && profile == target &&
            PlayerList.SelectedItem is PlayerRow selected && selected.SteamId == row.SteamId;
        try
        {
            await syncGate.WaitAsync(token); held = true;
            if (!StillSelected()) return;
            var inventory = await readInventory(target, row.SteamId, wipe, token);
            if (!StillSelected()) return;
            if (inventory.Source != "live" || inventory.SteamId != row.SteamId || inventory.WipeId != wipe || server?.WipeId != wipe ||
                !DateTimeOffset.TryParse(inventory.CapturedAt, out _))
                throw new InvalidDataException("現在の所持品の取得結果が一致しません。前回の表示を保持しています。");
            store.SaveInventory(target.Key, inventory);
            ShowInventory(inventory);
            Status(row.Name + " • 現在の所持品を取得しました（" + Time(inventory.CapturedAt) + "）");
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
