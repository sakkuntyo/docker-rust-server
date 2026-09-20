using System.IO;
using System.Windows;
using System.Windows.Controls;
using RustMonitor.Core;
using RustMonitor.Controls;

namespace RustMonitor;

public partial class ItemContentsWindow : Window
{
    private readonly SshProfile target;
    private readonly string steamId, wipe, itemUid;
    private readonly ItemIcons icons;
    private readonly Func<SshProfile, string, string, CancellationToken, Task<InventorySnapshot>> reader;
    private readonly CancellationTokenSource lifetime = new();
    private InventorySnapshot snapshot;
    private ItemRecord item;
    private bool closed, refreshing;
    private int generation;
    private readonly HashSet<string> deletedItems = [];
    public event Action<ItemRecord, InventorySnapshot>? OpenRequested;
    public event Action<ModerationRequest, string>? DeleteRequested;
    public ItemContentsWindow(SshProfile target, string playerName, string serverName, InventorySnapshot snapshot, ItemRecord item, ItemIcons icons,
        Func<SshProfile, string, string, CancellationToken, Task<InventorySnapshot>>? reader = null)
    {
        InitializeComponent();
        this.target = target; this.icons = icons; this.reader = reader ?? DockerSsh.ReadInventoryAsync;
        this.snapshot = Wire.Read<InventorySnapshot>(Wire.Write(snapshot)); this.item = Wire.Read<ItemRecord>(Wire.Write(item));
        steamId = snapshot.SteamId; wipe = snapshot.WipeId; itemUid = item.Uid;
        ItemCatalog.Name(this.item); Title = "Rust Monitor / 中身 / " + this.item.Name;
        ItemHeading.Text = this.item.Name; PlayerHeading.Text = playerName;
        DestinationText.Text = serverName + "\n" + target.Target + " / " + target.Container;
        Draw();
        RefreshButton.IsEnabled = ulong.TryParse(itemUid, out var uid) && uid > 0;
        StatusText.Text = RefreshButton.IsEnabled ? "表示中の記録です。現在の中身を取得します…" : "古い記録のため直接更新できません。メイン画面の「↻」で更新してから開き直してください。";
        Loaded += async (_, _) => { if (RefreshButton.IsEnabled) await RefreshAsync(); };
        Closed += (_, _) => { closed = true; generation++; lifetime.Cancel(); lifetime.Dispose(); };
    }
    private void Draw(bool missing = false)
    {
        generation++; var current = generation; var displayedSnapshot = snapshot;
        ContentsGrid.Children.Clear();
        var slots = missing ? [] : InventoryLayout.ContentSlots(item);
        ContentsGrid.Height = Math.Ceiling(slots.Count / 6d) * 64;
        foreach (var slot in slots)
        {
            var cell = InventoryItemView.Create(slot, icons, () => !closed && generation == current);
            if (slot.Item is { } content)
            {
                var menu = new ContextMenu { Style = (Style)FindResource("HistoryMenuStyle"), MinWidth = 220 };
                ModerationRequest? action = null;
                try { if (slot.PositionKnown) action = ModerationRequest.DeleteItem(displayedSnapshot, content, PlayerHeading.Text); }
                catch (ArgumentException) { }
                var remove = new MenuItem { Header = action == null ? "削除（↻で更新が必要）" : "削除", IsEnabled = action != null,
                    Foreground = System.Windows.Media.Brushes.LightCoral, Style = (Style)FindResource("HistoryItemStyle") };
                remove.Click += (_, _) => { if (action != null && !closed && generation == current) DeleteRequested?.Invoke(action, displayedSnapshot.CapturedAt); };
                menu.Items.Add(remove); cell.ContextMenu = menu;
            }
            if (slot.Item is { } child && InventoryLayout.CanOpen(child))
            {
                var menu = cell.ContextMenu;
                var open = new MenuItem { Header = "開く", Style = (Style)FindResource("HistoryItemStyle") };
                open.Click += (_, _) => OpenRequested?.Invoke(child, displayedSnapshot);
                menu.Items.Insert(0, open);
            }
            ContentsGrid.Children.Add(cell);
        }
        EmptyText.Text = missing ? "このアイテムは現在の所持品にありません。" : "中身は空です";
        EmptyText.Visibility = slots.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        CountText.Text = missing ? "" : item.Contents.Count + " 個";
        var time = DateTimeOffset.TryParse(snapshot.CapturedAt, out var at) ? at.ToLocalTime().ToString("yyyy/MM/dd HH:mm:ss") : "未記録";
        SnapshotText.Text = (snapshot.Source == "live" ? "直接取得 " : "セーブの記録 ") + time;
    }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();
    public void ApplyDeletion(SshProfile profile, ModerationRequest action)
    {
        if (closed || profile != target || action.SteamId != steamId || action.WipeId != wipe || action.Item == null) return;
        deletedItems.Add(action.Item.Uid);
        InventoryTree.RemoveWhere(snapshot.Items, i => deletedItems.Contains(i.Uid));
        InventoryTree.RemoveWhere(item.Contents, i => deletedItems.Contains(i.Uid));
        Draw(!Descendants(snapshot.Items, 0).Any(i => i.Uid == itemUid));
        StatusText.Text = "削除を反映しました。現在の中身を再取得します…";
        _ = RefreshAsync();
    }
    public async Task RefreshAsync()
    {
        if (closed || refreshing || !ulong.TryParse(itemUid, out var uid) || uid == 0) return;
        refreshing = true; RefreshButton.IsEnabled = false; StatusText.Text = "現在の中身を取得しています…";
        try
        {
            var fresh = await reader(target, steamId, wipe, lifetime.Token);
            if (closed) return;
            if (fresh.Source != "live" || fresh.SteamId != steamId || fresh.WipeId != wipe || !DateTimeOffset.TryParse(fresh.CapturedAt, out _))
                throw new InvalidDataException("プレイヤーまたはワイプが一致しません。");
            fresh = Wire.Read<InventorySnapshot>(Wire.Write(fresh));
            InventoryTree.RemoveWhere(fresh.Items, i => deletedItems.Contains(i.Uid));
            var matches = Descendants(fresh.Items, 0).Where(i => i.Uid == itemUid).ToArray();
            if (matches.Length > 1) throw new InvalidDataException("アイテムを特定できません。");
            snapshot = Wire.Read<InventorySnapshot>(Wire.Write(fresh));
            if (matches.Length == 0) { Draw(true); StatusText.Text = "移動・削除などにより、現在の所持品に見つかりません。"; return; }
            item = Wire.Read<ItemRecord>(Wire.Write(matches[0]));
            Draw(); StatusText.Text = "現在の中身を取得しました。";
        }
        catch (Exception ex) { if (!closed) StatusText.Text = "更新できませんでした。前回の表示を保持しています。\n" + (ex is IOException or ArgumentException or InvalidOperationException ? ex.Message : "接続先を確認してください。"); }
        finally { refreshing = false; if (!closed) RefreshButton.IsEnabled = true; }
    }
    private static IEnumerable<ItemRecord> Descendants(IEnumerable<ItemRecord> items, int depth)
    {
        if (depth > 6) yield break;
        foreach (var child in items)
        {
            yield return child;
            foreach (var nested in Descendants(child.Contents, depth + 1)) yield return nested;
        }
    }
}
