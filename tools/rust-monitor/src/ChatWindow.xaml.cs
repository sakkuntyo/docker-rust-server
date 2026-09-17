using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using RustMonitor.Core;

namespace RustMonitor;

public partial class ChatWindow : Window
{
    private readonly Store store;
    private readonly SshProfile profile;
    private readonly Func<SshProfile, CancellationToken, Task<ChatSnapshot>> reader;
    private readonly Func<SshProfile, string, CancellationToken, Task<ChatSendResult>> sender;
    private readonly CancellationTokenSource lifetime = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(5) };
    private ChatSnapshot chat;
    private bool ready, closed, reading, verified, automatic = true;
    private string rendered = "";
    public bool IsSending { get; private set; }
    public string Draft => ChatInput.Text;

    public ChatWindow(Store store, SshProfile profile, string serverName, string draft = "",
        Func<SshProfile, CancellationToken, Task<ChatSnapshot>>? reader = null,
        Func<SshProfile, string, CancellationToken, Task<ChatSendResult>>? sender = null)
    {
        InitializeComponent(); this.store = store; this.profile = profile;
        this.reader = reader ?? DockerSsh.ReadChatAsync; this.sender = sender ?? DockerSsh.SendChatAsync;
        Title = "Rust Monitor / チャット / " + serverName;
        ChatDestination.Text = "送信先：" + serverName; ConnectionText.Text = profile.Target + " / " + profile.Container;
        ChatGlobalOnly.IsChecked = store.Get("chat-global-only") != "false";
        chat = store.Get("chat:" + profile.Key) is string json ? Wire.Read<ChatSnapshot>(json) : new();
        ChatInput.Text = draft; ready = true; BindChat(); UpdateControls();
        ChatStateText.Text = chat.CheckedAt.Length > 0 ? "保存済み • 最終取得 " + Time(chat.CheckedAt) : "取得待ち";
        timer.Tick += async (_, _) => await RefreshAsync();
        Loaded += async (_, _) => { if (!closed && automatic) { timer.Start(); await RefreshAsync(); } };
        Closing += (_, e) => { if (IsSending) { e.Cancel = true; ChatSendStatus.Text = "送信結果を確認しています。完了後に閉じてください。"; } };
        Closed += (_, _) => { closed = true; timer.Stop(); lifetime.Cancel(); lifetime.Dispose(); };
    }
    private static string Time(string value) => DateTimeOffset.TryParse(value, out var time) ? time.ToLocalTime().ToString("yyyy/MM/dd HH:mm:ss") : "未記録";
    private void UpdateControls()
    {
        if (!ready || closed) return;
        ChatInput.IsEnabled = !IsSending;
        ChatSend.IsEnabled = verified && automatic && !IsSending && !string.IsNullOrWhiteSpace(ChatInput.Text);
        RefreshButton.IsEnabled = !reading && !IsSending;
        PauseButton.IsEnabled = !IsSending;
    }
    private void SetReadStatus() => ChatStateText.Text = (automatic ? "5秒ごとに更新" : "更新停止中 • 送信は更新再開後に利用できます") +
        " • 最新 " + chat.Messages.Count + " 件\n最終取得 " + Time(chat.CheckedAt);
    public async Task RefreshAsync()
    {
        if (closed || reading) return;
        reading = true; UpdateControls();
        try
        {
            var snapshot = await reader(profile, lifetime.Token);
            if (closed) return;
            chat = snapshot; verified = true;
            store.Put("chat:" + profile.Key, Wire.Write(snapshot)); BindChat(); SetReadStatus();
        }
        catch (Exception ex)
        {
            if (!closed) { verified = false; ChatStateText.Text = "チャット取得待ち • 取得済みの履歴を表示しています。\n" + ex.Message; }
        }
        finally { reading = false; UpdateControls(); }
    }
    private void BindChat()
    {
        var rows = chat.Messages.Where(m => ChatGlobalOnly.IsChecked != true || m.Channel is 0 or 2).ToList();
        var content = Wire.Write(rows);
        if (content == rendered) return;
        var followLatest = ChatScroll.ScrollableHeight - ChatScroll.VerticalOffset < 24 || rendered.Length == 0;
        rendered = content; ChatMessages.ItemsSource = rows;
        ChatEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (followLatest) _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => { if (!closed) ChatScroll.ScrollToEnd(); });
    }
    private void ChatFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (!ready) return;
        store.Put("chat-global-only", ChatGlobalOnly.IsChecked == true ? "true" : "false"); rendered = ""; BindChat();
    }
    private void ChatInput_Changed(object sender, TextChangedEventArgs e) => UpdateControls();
    private async void ChatInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.Control) return;
        e.Handled = true; await SendAsync();
    }
    private async void ChatSend_Click(object sender, RoutedEventArgs e) => await SendAsync();
    public async Task SendAsync()
    {
        if (closed || !verified || !automatic || IsSending) return;
        string message;
        try { message = ChatText.Validate(ChatInput.Text); }
        catch (ArgumentException ex) { ChatSendStatus.Text = ex.Message; return; }
        IsSending = true; UpdateControls(); ChatSendStatus.Text = "送信しています…";
        var accepted = false;
        try
        {
            // The destination is fixed for this window; never retry an uncertain broadcast.
            var result = await sender(profile, message, CancellationToken.None);
            accepted = result.Accepted;
            if (accepted) { ChatInput.Clear(); ChatSendStatus.Text = "送信しました"; }
            else ChatSendStatus.Text = "送信結果を確認できません。再送する前にチャットを確認してください。";
        }
        catch (Exception) { ChatSendStatus.Text = "送信結果を確認できません。再送する前にチャットを確認してください。"; }
        finally { IsSending = false; UpdateControls(); }
        if (accepted) await RefreshAsync();
    }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();
    private async void Pause_Click(object sender, RoutedEventArgs e)
    {
        if (IsSending) return;
        automatic = !automatic; PauseButton.Content = automatic ? "更新停止" : "更新再開";
        verified = false; UpdateControls(); SetReadStatus();
        if (automatic) { timer.Start(); await RefreshAsync(); } else timer.Stop();
    }
}
