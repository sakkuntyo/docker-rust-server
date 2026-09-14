using System.Windows;
using RustMonitor.Core;

namespace RustMonitor;
public partial class DockerOverviewWindow : Window
{
    private readonly Store store;
    private readonly CancellationTokenSource lifetime = new();
    private bool closed;
    public DockerServer? SelectedServer { get; private set; }
    public string Target => TargetBox.Text.Trim();
    public DockerOverviewWindow(Store database)
    {
        InitializeComponent(); store = database;
        ServersGrid.MouseDoubleClick += (_, e) =>
        {
            var element = e.OriginalSource as DependencyObject;
            while (element != null && element is not System.Windows.Controls.DataGridRow)
                element = element is FrameworkContentElement content ? content.Parent : System.Windows.Media.VisualTreeHelper.GetParent(element);
            if (element is System.Windows.Controls.DataGridRow row && row.Item is DockerServer server)
            { SelectedServer = server; DialogResult = true; }
        };
        TargetBox.Text = store.Get("last-ssh-target") ?? "";
        if (store.Get("docker-report:" + TargetBox.Text) is string saved) Display(Wire.Read<DockerReport>(saved), false);
        Closed += (_, _) => { closed = true; lifetime.Cancel(); };
    }
    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        UpdateButton.IsEnabled = false; TargetBox.IsEnabled = false;
        var target = TargetBox.Text.Trim();
        StatusText.Text = "SSH で人数・シーズン・Docker ログを集計しています…";
        try
        {
            var report = await DockerSsh.ReadOverviewAsync(target, lifetime.Token);
            if (closed) return;
            DockerSsh.Remember(store, target, report); Display(report, true);
        }
        catch (Exception ex) { if (!closed) StatusText.Text = "更新できませんでした。表示済みの人数は現在値ではありません。 " + ex.Message; }
        finally { if (!closed) { UpdateButton.IsEnabled = true; TargetBox.IsEnabled = true; } }
    }
    private void Display(DockerReport report, bool fresh)
    {
        ServersGrid.ItemsSource = report.Servers;
        var local = DateTimeOffset.TryParse(report.CheckedAt, out var time) ? time.ToLocalTime().ToString("yyyy/MM/dd HH:mm:ss") : "未確認";
        StatusText.Text = (fresh ? "取得時点 " : "保存済みデータ / 取得時点 ") + local + " • " + report.Servers.Count + " サーバー";
        if (report.Servers.Any(s => s.PartialLogs)) StatusText.Text += " • シーズン途中からのログしかないサーバーがあります。";
    }
}
