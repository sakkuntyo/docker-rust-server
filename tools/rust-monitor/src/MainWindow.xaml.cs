using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RustMonitor.Core;

namespace RustMonitor;

public partial class MainWindow : Window
{
    private readonly Store store;
    private readonly string root;
    private readonly Func<SshProfile, CancellationToken, Task<SshServerSnapshot>> readServer;
    private readonly Func<SshProfile, string, string, CancellationToken, Task<InventorySnapshot>> readInventory;
    private readonly Action<Uri> openLink;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly SemaphoreSlim syncGate = new(1, 1);
    private CancellationTokenSource session = new();
    private SshProfile? profile;
    private ServerState? server;
    private List<PlayerRecord> players = [];
    private bool live, closed, bindingRoster, presenceAvailable, serverBusy;
    private int generation;

    public MainWindow(string dataRoot, Func<SshProfile, CancellationToken, Task<SshServerSnapshot>>? serverReader = null,
        Func<SshProfile, CancellationToken, Task<ChatSnapshot>>? chatReader = null,
        Func<SshProfile, string, CancellationToken, Task<ChatSendResult>>? chatSender = null, ItemIcons? itemIcons = null,
        Action<Uri>? linkOpener = null,
        Func<SshProfile, string, string, CancellationToken, Task<InventorySnapshot>>? inventoryReader = null)
    {
        InitializeComponent();
        openLink = linkOpener ?? (uri => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }));
        root = dataRoot;
        icons = itemIcons ?? new ItemIcons(Path.Combine(root, "icons"));
        readServer = serverReader ?? DockerSsh.ReadServerAsync;
        readInventory = inventoryReader ?? DockerSsh.ReadInventoryAsync;
        readChat = chatReader ?? DockerSsh.ReadChatAsync;
        sendChat = chatSender ?? DockerSsh.SendChatAsync;
        store = new Store(Path.Combine(root, "monitor.sqlite3"));
        InitializeInventory();
        timer.Tick += async (_, _) => { if (live) await RefreshSafeAsync(); };
        Closing += (_, e) => { if (moderationWindow?.IsSending == true || itemWindows.Values.Any(w => w.IsSending) || chatWindows.Values.Any(w => w.IsSending)) { e.Cancel = true; Status("送信結果を確認しています。完了後に閉じてください。"); } };
        Closed += (_, _) => { closed = true; generation++; foreach (var window in chatWindows.Values.ToArray()) window.Close(); foreach (var window in itemWindows.Values.ToArray()) window.Close(); foreach (var window in combatWindows.Values.ToArray()) window.Close(); icons.Dispose(); timer.Stop(); session.Cancel(); session.Dispose(); store.Dispose(); };
        TargetBox.Text = store.Get("last-ssh-target") ?? "";
        if (store.Get("ssh-list:" + TargetBox.Text) is string list) SetServers(Wire.Read<DockerReport>(list));
        else if (store.Get("docker-report:" + TargetBox.Text) is string report) SetServers(Wire.Read<DockerReport>(report));
        if (store.Get("last-ssh-profile") is string previous) LoadProfile(Wire.Read<SshProfile>(previous));
        SetBusy(false);
    }
    private static string Time(string value) => DateTimeOffset.TryParse(value, out var time) ? time.ToLocalTime().ToString("yyyy/MM/dd HH:mm:ss") : "未記録";
    private static string SafeError(Exception ex) => ex is IOException or InvalidDataException or ArgumentException or InvalidOperationException ? ex.Message : "応答形式または SSH 接続を確認してください。";
    private void Status(string text) { if (!closed) StatusText.Text = text; }
    private void SetBusy(bool busy)
    {
        serverBusy = busy;
        ConnectButton.IsEnabled = ListButton.IsEnabled = !busy;
        TargetBox.IsEnabled = ContainerBox.IsEnabled = !busy;
        ChatButton.IsEnabled = profile != null;
        RefreshButton.IsEnabled = live && !busy;
        DisconnectButton.IsEnabled = live || busy;
        UpdateInventoryRefreshButton();
    }
    private void SetServers(DockerReport report)
    {
        var selected = (ContainerBox.SelectedItem as DockerServer)?.Container ?? profile?.Container;
        ContainerBox.ItemsSource = report.Servers;
        ContainerBox.SelectedItem = report.Servers.FirstOrDefault(s => s.Container == selected) ?? report.Servers.FirstOrDefault();
    }
    private void LoadProfile(SshProfile next)
    {
        var changed = profile != next;
        profile = next;
        TargetBox.Text = next.Target;
        server = store.Get("server:" + next.Key) is string json ? Wire.Read<ServerState>(json) : null;
        var savedList = store.Get("ssh-list:" + next.Target) ?? store.Get("docker-report:" + next.Target);
        var list = savedList != null ? Wire.Read<DockerReport>(savedList).Servers : [];
        if (!list.Any(s => s.Container == next.Container)) list.Add(new DockerServer { Container = next.Container, Name = server?.Name is { Length: > 0 } name ? name : next.Container });
        ContainerBox.ItemsSource = list;
        ContainerBox.SelectedItem = list.First(s => s.Container == next.Container);
        players = store.Players(next.Key);
        if (changed)
        {
            bindingRoster = true;
            try { SearchBox.Clear(); OnlineOnly.IsChecked = false; }
            finally { bindingRoster = false; }
        }
        presenceAvailable = false;
        BindRoster(); LoadMap(); ShowSelectedInventory();
        Status(server == null ? "未取得 • SSH で接続してください" : "保存済みデータ • 最終同期 " + Time(server.CapturedAt));
    }
    private void Disconnect()
    {
        generation++; timer.Stop(); session.Cancel(); session.Dispose(); session = new();
        live = false; presenceAvailable = false; SetBusy(false); BindRoster(); ShowSelectedInventory();
    }
    private async void List_Click(object sender, RoutedEventArgs e)
    {
        Disconnect();
        var current = generation;
        var target = TargetBox.Text.Trim();
        SetBusy(true); Status("SSH でコンテナ一覧を取得しています…");
        try
        {
            var report = await DockerSsh.ListServersAsync(target, session.Token);
            if (closed || current != generation) return;
            SetServers(report); store.Put("ssh-list:" + target, Wire.Write(report)); store.Put("last-ssh-target", target);
            Status(report.Servers.Count == 0 ? "稼働中の rust-* コンテナが見つかりません。" : "サーバーを選択して「SSH で接続」を押してください。");
        }
        catch (Exception ex) { if (!closed && current == generation) Status(SafeError(ex)); }
        finally { if (!closed && current == generation) SetBusy(false); }
    }
    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (ContainerBox.SelectedItem is not DockerServer selected) { Status("「一覧取得」でサーバーを選択してください。"); return; }
        var next = new SshProfile(TargetBox.Text.Trim(), selected.Container);
        await ConnectProfileAsync(next);
    }
    private async Task ConnectProfileAsync(SshProfile next)
    {
        if (closed) return;
        if (!DockerSsh.ValidTarget(next.Target) || !DockerSsh.ValidContainer(next.Container)) { Status("SSH 接続先とコンテナを確認してください。"); return; }
        Disconnect(); LoadProfile(next);
        await RefreshSafeAsync(true);
    }
    private void Disconnect_Click(object sender, RoutedEventArgs e) { Disconnect(); Status("更新を停止しました • 保存済みデータを表示しています。"); }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshSafeAsync(true);
    private async Task RefreshSafeAsync(bool wait = false)
    {
        if (profile == null || closed) return;
        var current = generation; var next = profile;
        if (wait) await syncGate.WaitAsync();
        else if (!await syncGate.WaitAsync(0)) return;
        if (closed || current != generation) { syncGate.Release(); return; }
        SetBusy(true); Status("SSH / docker exec でメンバーと所持品を取得しています…");
        try
        {
            var snapshot = await readServer(next, session.Token);
            if (closed || current != generation) return;
            store.SaveSshSnapshot(next, snapshot);
            server = snapshot.Server; players = store.Players(next.Key);
            live = true; presenceAvailable = snapshot.PresenceAvailable;
            BindRoster(); LoadMap(); ShowSelectedInventory();
            string mapWarning = "";
            try { await EnsureMapAsync(next, current); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or FormatException or NotSupportedException)
            {
                if (!closed && current == generation) { MapReason.Text = SafeError(ex); mapWarning = " • マップ取得待ち"; }
            }
            if (closed || current != generation) return;
            var profiles = store.Get("ssh-profiles") is string saved ? Wire.Read<List<SshProfile>>(saved) : [];
            profiles.RemoveAll(p => p.Key == next.Key); profiles.Insert(0, next);
            store.Put("ssh-profiles", Wire.Write(profiles)); store.Put("last-ssh-profile", Wire.Write(next)); store.Put("last-ssh-target", next.Target);
            Status("SSH 同期 " + Time(server.CapturedAt) + " • 30 秒ごとに更新" + mapWarning + (snapshot.Warning.Length > 0 ? "\n" + snapshot.Warning : ""));
            timer.Start();
        }
        catch (Exception ex)
        {
            if (!closed && current == generation) { Disconnect(); Status("更新できませんでした • " + SafeError(ex)); }
        }
        finally { syncGate.Release(); if (!closed && current == generation) SetBusy(false); }
    }
    private void Filter_Changed(object sender, RoutedEventArgs e) { if (IsInitialized && PlayerList != null && !bindingRoster) BindRoster(); }
    private void BindRoster()
    {
        if (PlayerList == null) return;
        var selected = (PlayerList.SelectedItem as PlayerRow)?.SteamId;
        var search = SearchBox.Text.Trim();
        var rows = players.Select(p => new PlayerRow(p, live)).Where(p =>
            (OnlineOnly.IsChecked != true || p.IsOnline) && (search.Length == 0 || p.Name.Contains(search, StringComparison.OrdinalIgnoreCase) || p.SteamId.Contains(search)))
            .OrderByDescending(p => p.IsOnline).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        bindingRoster = true;
        try { PlayerList.ItemsSource = rows; PlayerList.SelectedItem = rows.FirstOrDefault(p => p.SteamId == selected); }
        finally { bindingRoster = false; }
        RosterEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RosterEmpty.Text = players.Count == 0 ? "SSH で接続すると、接続中の人と過去の接続履歴を表示します。" : "条件に一致するメンバーがいません。";
        ServerTitle.Text = server?.Name is { Length: > 0 } name ? name : "SSH 接続先とサーバーを選択してください";
        CountsText.Text = live ? (presenceAvailable ? $"オンライン {players.Count(p => new PlayerRow(p, true).IsOnline)}" : "オンライン 未確認") + $" / 記録済み {players.Count}"
            : $"保存済み {players.Count} 人 • 接続状態は未確認";
        ShowSelectedInventory();
    }
    private void Player_Selected(object sender, SelectionChangedEventArgs e) { if (!bindingRoster) ShowSelectedInventory(); }
    private void ShowSelectedInventory()
    {
        UpdateMapLayout();
        CombatLogButton.IsEnabled = profile != null && PlayerList.SelectedItem is PlayerRow;
        GiveItemButton.IsEnabled = CombatLogButton.IsEnabled;
        BanButton.IsEnabled = CombatLogButton.IsEnabled;
        UpdateInventoryRefreshButton();
        InventoryItems.Children.Clear();
        if (PlayerList.SelectedItem is not PlayerRow row || profile == null)
        {
            InventoryName.Text = "メンバーを選択"; PlayerSteamId.Text = PlayerIp.Text = PlayerDetails.Text = "";
            SetPlayerLinks(null); ShowInventory(null); return;
        }
        InventoryName.Text = row.Name;
        PlayerSteamId.SetDisplayText(row.SteamIdLabel);
        PlayerIp.SetDisplayText(row.IpText);
        SetPlayerLinks(row);
        var details = (row.IpObservation.Length > 0 ? row.IpObservation + "\n" : "") + "初回確認 " + Time(row.Record.FirstSeen) + "\n最終オンライン確認 " + Time(row.Record.LastSeen);
        details += row.Record.WipeId == server?.WipeId && row.Record.X != null && row.Record.Z != null && row.Record.PositionAt.Length > 0
            ? "\n" + Coordinates(row.Record) + "\n座標のセーブ " + Time(row.Record.PositionAt)
            : "\n座標：" + (row.Record.PositionReason.Length > 0 ? row.Record.PositionReason : "最新セーブに記録がありません");
        PlayerDetails.SetDisplayText(details);
        ShowInventory(store.Inventory(profile.Key, server?.WipeId ?? "", row.SteamId));
    }
    private void SetPlayerLinks(PlayerRow? row)
    {
        SteamProfileLink.NavigateUri = row?.SteamProfileUri;
        IpInfoLink.NavigateUri = row?.IpInfoUri;
        SteamProfileLinkHost.Visibility = SteamProfileLink.NavigateUri == null ? Visibility.Collapsed : Visibility.Visible;
        IpInfoLinkHost.Visibility = IpInfoLink.NavigateUri == null ? Visibility.Collapsed : Visibility.Visible;
    }
    private void PlayerLink_Navigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        e.Handled = true;
        if (sender is not System.Windows.Documents.Hyperlink link || link.NavigateUri is not { } uri) return;
        try { openLink(uri); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        { Status("ブラウザーを開けませんでした。Windowsの既定のブラウザー設定を確認してください。"); }
    }
    private void ShowInventory(InventorySnapshot? snapshot)
    {
        shownInventory = snapshot;
        inventoryGeneration++;
        InventoryItems.Children.Clear();
        inventoryAvailable = snapshot != null && !string.IsNullOrEmpty(snapshot.CapturedAt);
        UpdateInventoryDetailsVisibility();
        if (!inventoryAvailable)
        { InventoryStatus.Text = PlayerList.SelectedItem == null ? "メンバーを選ぶと、持ち物を表示します。" : "このワイプの所持品は未取得です。最新セーブに本人の身体がない場合もあります。"; return; }
        InventoryStatus.Text = snapshot!.Source == "live"
            ? "サーバーから直接取得した所持品\n" + Time(snapshot.CapturedAt) + "\n取得後の変化は「↻」で更新できます。"
            : snapshot.Source == "save"
            ? (server?.SaveAt == snapshot.CapturedAt ? "最終セーブ時点の所持品" : "以前のセーブの所持品") + "\n" + Time(snapshot.CapturedAt) + "\nセーブ後の変更は次の保存で反映されます。"
            : "最終取得時点の記録（現在の所持品は未確認）\n" + Time(snapshot.CapturedAt);
        // Keep the saved snapshot intact. A confirmed deletion is newer evidence
        // and must also win over subsequent reads of an older server save.
        var visibleItems = snapshot.Items.Where(item => !WasDeleted(snapshot, item)).ToList();
        if (visibleItems.Count != snapshot.Items.Count)
            InventoryStatus.Text += "\n削除成功済みのアイテムは一覧から除外しています。";
        DrawInventory(visibleItems);
    }
    private string MapPath(string key, string wipe) => Path.Combine(root, "maps", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key + "\n" + wipe))) + ".jpg");
    private void LoadMap()
    {
        var key = profile?.Key + "\n" + server?.WipeId;
        if (key == loadedMapKey && MapImage.Source != null) { UpdateMapLayout(); return; }
        if (key != loadedMapKey) { loadedMapKey = key; mapView.Reset(); }
        MapImage.Source = null; MapEmpty.Visibility = Visibility.Visible;
        UpdateMapLayout();
        MapMeta.Text = server == null ? "" : $"{server.Size} m • Seed {server.Seed}";
        MapReason.Text = "今季のマップ画像を SSH 経由で取得します。";
        if (server == null || profile == null) return;
        var path = MapPath(profile.Key, server.WipeId);
        if (!File.Exists(path)) return;
        try
        {
            MapImage.Source = DecodeMap(File.ReadAllBytes(path));
            MapEmpty.Visibility = Visibility.Collapsed;
            UpdateMapLayout();
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or FileFormatException) { MapReason.Text = "保存済みマップを読み込めません。再取得します。"; }
    }
    private static BitmapImage DecodeMap(byte[] bytes)
    {
        if (bytes.Length == 0 || bytes.Length > 16000000) throw new InvalidDataException("マップ画像のサイズが不正です。");
        var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = new MemoryStream(bytes); bitmap.EndInit(); bitmap.Freeze();
        if (bitmap.PixelWidth > 10000 || bitmap.PixelHeight > 10000) throw new InvalidDataException("マップ画像の解像度が大きすぎます。");
        return bitmap;
    }
    private async Task EnsureMapAsync(SshProfile next, int current)
    {
        if (server == null || MapImage.Source != null) return;
        if (!server.MapAvailable) { MapReason.Text = "今季のマップ画像はまだ記録されていません。"; return; }
        var wipe = server.WipeId;
        var map = await DockerSsh.ReadMapAsync(next, wipe, session.Token);
        if (closed || current != generation) return;
        if (map.WipeId != wipe) throw new InvalidDataException("マップのワイプが変わりました。再取得してください。");
        var bytes = Convert.FromBase64String(map.Data); var bitmap = DecodeMap(bytes);
        var path = MapPath(next.Key, wipe); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path + ".tmp", bytes); File.Move(path + ".tmp", path, true);
        MapImage.Source = bitmap; MapEmpty.Visibility = Visibility.Collapsed;
        UpdateMapLayout();
    }
    private void Profiles_Click(object sender, RoutedEventArgs e)
    {
        if (HistoryButton.ContextMenu is { IsOpen: true } previous) { previous.IsOpen = false; return; }
        var menu = new ContextMenu { Style = (Style)FindResource("HistoryMenuStyle"), Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        var profiles = store.Get("ssh-profiles") is string json ? Wire.Read<List<SshProfile>>(json) : [];
        foreach (var saved in profiles.Distinct())
        {
            var cached = store.Get("server:" + saved.Key) is string state ? Wire.Read<ServerState>(state) : null;
            var header = new StackPanel();
            header.Children.Add(new TextBlock { Text = cached?.Name is { Length: > 0 } name ? name : saved.Container,
                Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            header.Children.Add(new TextBlock { Text = saved.Target + " / " + saved.Container, Foreground = Brushes.LightSteelBlue, FontSize = 11, Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap });
            header.Children.Add(new TextBlock { Text = "前回同期 " + Time(cached?.CapturedAt ?? "") + " • クリックで接続", Foreground = Brushes.LightSlateGray, FontSize = 11, Margin = new Thickness(0, 3, 0, 0) });
            var item = new MenuItem { Header = header, Tag = saved, Style = (Style)FindResource("HistoryItemStyle") };
            System.Windows.Automation.AutomationProperties.SetName(item, (cached?.Name ?? saved.Container) + " / " + saved.Target + " / " + saved.Container);
            item.Click += async (_, args) => { args.Handled = true; menu.IsOpen = false; await ConnectProfileAsync(saved); };
            menu.Items.Add(item);
        }
        if (profiles.Count == 0) menu.Items.Add(new MenuItem { Header = "接続履歴はまだありません。SSH で接続すると追加されます。", IsEnabled = false, Style = (Style)FindResource("HistoryItemStyle") });
        HistoryButton.ContextMenu = menu;
        menu.PlacementTarget = HistoryButton; menu.IsOpen = true;
    }
    private async void Docker_Click(object sender, RoutedEventArgs e)
    {
        var overview = new DockerOverviewWindow(store) { Owner = this };
        if (overview.ShowDialog() == true && overview.SelectedServer is DockerServer selected)
        {
            Disconnect(); LoadProfile(new SshProfile(overview.Target, selected.Container));
            await RefreshSafeAsync(true);
        }
    }
    public sealed class PlayerRow(PlayerRecord record, bool connected)
    {
        public PlayerRecord Record => record;
        public string Name => record.Name;
        public string SteamId => record.SteamId;
        public string SteamIdLabel => "Steam ID: " + record.SteamId;
        public Uri? SteamProfileUri => SteamId.Length == 17 && SteamId.All(char.IsAsciiDigit)
            ? new Uri("https://steamcommunity.com/profiles/" + SteamId) : null;
        private bool HasIp => System.Net.IPAddress.TryParse(record.RealIp, out _) && DateTimeOffset.TryParse(record.IpCheckedAt, out _);
        public Uri? IpInfoUri => HasIp ? new Uri("https://ipinfo.io/" + Uri.EscapeDataString(System.Net.IPAddress.Parse(record.RealIp).ToString())) : null;
        public bool CurrentIp => IsOnline && record.IpVerified && DateTimeOffset.TryParse(record.IpCheckedAt, out var time) && Math.Abs((DateTimeOffset.UtcNow - time).TotalMinutes) < 3;
        public string IpText => "本IP: " + (HasIp ? record.RealIp + (CurrentIp ? "" : "（最終確認）") : "未確認");
        public string IpObservation => ((HasIp ? "conntrack 確認 " + Time(record.IpCheckedAt) : "") +
            (IsOnline && !CurrentIp && record.IpReason.Length > 0 ? "\n" + record.IpReason : !HasIp && !IsOnline ? "\n接続中に確認できたIPを記録します。" : "")).TrimStart('\n');
        public string IpDetails => IpText + (IpObservation.Length > 0 ? "\n" + IpObservation : "");
        public bool Fresh => connected && DateTimeOffset.TryParse(record.ObservedAt, out var time) && DateTimeOffset.UtcNow - time < TimeSpan.FromMinutes(3);
        public bool IsOnline => Fresh && record.Online;
        public string State => !Fresh ? "未確認" : record.Online ? "オンライン" : "オフライン";
        public string Color => IsOnline ? "#8ECBAD" : "#9DADB9";
        public string LastSeen => "最終オンライン確認 " + Time(record.LastSeen);
    }
}
