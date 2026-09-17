using System.Windows;
using RustMonitor.Core;

namespace RustMonitor;

public partial class ModerationWindow : Window
{
    private readonly SshProfile target;
    private readonly ModerationRequest request;
    private readonly Func<SshProfile, ModerationRequest, CancellationToken, Task<ModerationResult>> sender;
    private bool attempted, closed;
    public bool IsSending { get; private set; }
    public ModerationResult? Result { get; private set; }

    public ModerationWindow(SshProfile profile, ModerationRequest action, string serverName, string capturedAt = "",
        Func<SshProfile, ModerationRequest, CancellationToken, Task<ModerationResult>>? send = null)
    {
        InitializeComponent();
        target = profile;
        // Keep an immutable copy even when the main selection / inventory refreshes.
        request = Wire.Read<ModerationRequest>(Wire.Write(action));
        sender = send ?? DockerSsh.ModerateAsync;
        var ban = request.Action == "ban";
        Title = "Rust Monitor / " + (ban ? "BAN" : "アイテム削除") + " / " + request.Name;
        Heading.Text = ban ? "プレイヤーをBAN" : "アイテムを削除";
        ExecuteButton.Content = ban ? "このプレイヤーをBAN" : "このアイテムを削除";
        TargetText.Text = serverName + "\n" + target.Target + " / " + target.Container + "\n\n" + request.Name + "\nSteam ID: " + request.SteamId;
        ReasonPanel.Visibility = ban ? Visibility.Visible : Visibility.Collapsed;
        ActionText.Text = ban ? "このサーバーで、対象のSteam IDを無期限BANします。\n接続中の場合は切断されます。" :
            request.Item?.Name + " × " + request.Item?.Amount + "\n" + ContainerLabel(request.Item?.Container) + " / スロット " + (request.Item?.Slot + 1) +
            "\n記録時刻：" + (DateTimeOffset.TryParse(capturedAt, out var time) ? time.ToLocalTime().ToString("yyyy/MM/dd HH:mm:ss") : "未確認") +
            "\n\nこのスタック全体とアタッチメント・内容物を削除します。保存後に移動・変更されていた場合は中止します。";
        Closing += (_, e) => { if (IsSending) { e.Cancel = true; ResultText.Text = "実行結果を確認しています。完了後に閉じてください。"; } };
        Closed += (_, _) => closed = true;
    }
    private static string ContainerLabel(string? key) => key == "main" ? "インベントリ" : key == "belt" ? "ベルト" : "装備";
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private async void Execute_Click(object sender, RoutedEventArgs e) => await ExecuteAsync();
    public async Task ExecuteAsync()
    {
        if (attempted || IsSending || closed) return;
        request.Reason = ReasonBox.Text.Trim();
        try { request.Validate(); }
        catch (ArgumentException ex) { ResultText.Text = ex.Message; return; }
        attempted = true; IsSending = true; ExecuteButton.IsEnabled = CloseButton.IsEnabled = ReasonBox.IsEnabled = false;
        ResultText.Text = "管理機能を準備して、操作を実行しています…";
        try { Result = await sender(target, request, CancellationToken.None); }
        catch (Exception) { Result = new ModerationResult { State = "unknown", Message = "操作結果を確認できません。自動再送しません。ゲーム内の所持品またはサーバーのBAN一覧を確認してください。" }; }
        finally { IsSending = false; CloseButton.IsEnabled = true; CloseButton.Content = "閉じる"; }
        ResultText.Text = Result.Message;
    }
}
