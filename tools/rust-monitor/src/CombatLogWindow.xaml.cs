using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using RustMonitor.Core;

namespace RustMonitor;

public partial class CombatLogWindow : Window
{
    private readonly Store store;
    private readonly SshProfile profile;
    private readonly string steamId;
    private readonly Func<SshProfile, string, CancellationToken, Task<CombatSnapshot>> reader;
    private readonly CancellationTokenSource lifetime = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMinutes(1) };
    private readonly CombatHistory history;
    private readonly ObservableCollection<CombatEntry> rows;
    private bool closed, reading, automatic = true;

    public CombatLogWindow(Store store, SshProfile profile, string steamId, string playerName, string serverName,
        Func<SshProfile, string, CancellationToken, Task<CombatSnapshot>>? reader = null)
    {
        InitializeComponent();
        this.store = store; this.profile = profile; this.steamId = steamId; this.reader = reader ?? DockerSsh.ReadCombatAsync;
        Title = "Rust Monitor / コンバットログ / " + playerName;
        PlayerHeading.Text = playerName + " • Steam ID: " + steamId;
        DestinationText.Text = serverName + "\n" + profile.Target + " / " + profile.Container;
        history = store.Get(CombatHistory.Key(profile, steamId)) is string json ? Wire.Read<CombatHistory>(json) : new();
        rows = new(history.Entries); LogGrid.ItemsSource = rows;
        UpdateCount();
        StatusText.Text = rows.Count > 0 ? "保存済みのログ • 接続して差分を取得します" : "取得待ち";
        timer.Tick += async (_, _) => await RefreshAsync();
        Loaded += async (_, _) => { if (!closed && automatic) { timer.Start(); await RefreshAsync(); } };
        Closed += (_, _) => { closed = true; timer.Stop(); lifetime.Cancel(); lifetime.Dispose(); };
    }

    private void UpdateCount()
    {
        CountText.Text = rows.Count + " 件 • " + (automatic ? "1分ごとに新しい記録を追記" : "自動更新停止中");
        EmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    public async Task RefreshAsync()
    {
        if (closed || reading) return;
        reading = true; RefreshButton.IsEnabled = false; StatusText.Text = "コンバットログを取得しています…";
        try
        {
            var snapshot = await reader(profile, steamId, lifetime.Token);
            if (closed) return;
            var result = history.Append(snapshot, steamId);
            foreach (var row in result.Added) rows.Add(row);
            while (rows.Count > CombatHistory.Limit) rows.RemoveAt(0);
            store.Put(CombatHistory.Key(profile, steamId), Wire.Write(history));
            UpdateCount();
            EmptyText.Text = snapshot.Available ? "サーバーに残っているコンバットログはありません" : "対象のプレイヤーは現在サーバー上で確認できません";
            StatusText.Text = "最終取得 " + DateTimeOffset.Parse(snapshot.CheckedAt).ToLocalTime().ToString("yyyy/MM/dd HH:mm:ss") +
                " • " + (result.Added.Count == 0 ? "新しい記録はありません" : result.Added.Count + " 件を追記") +
                (result.Notice.Length > 0 ? "\n" + result.Notice : "");
            if (result.Added.Count > 0 && FollowLatest.IsChecked == true)
                _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => { if (!closed && rows.Count > 0) LogGrid.ScrollIntoView(rows[^1]); });
        }
        catch (Exception ex)
        {
            if (!closed)
            {
                StatusText.Text = "更新できませんでした。取得済みのログを表示しています。" + (automatic ? "1分後に再取得します。" : "") + "\n" + ex.Message;
                if (rows.Count == 0) EmptyText.Text = "コンバットログの取得を待っています";
            }
        }
        finally { reading = false; if (!closed) RefreshButton.IsEnabled = true; }
    }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();
    private async void Pause_Click(object sender, RoutedEventArgs e)
    {
        automatic = !automatic; PauseButton.Content = automatic ? "更新停止" : "更新再開"; UpdateCount();
        if (automatic) { timer.Start(); await RefreshAsync(); }
        else timer.Stop();
    }
}
