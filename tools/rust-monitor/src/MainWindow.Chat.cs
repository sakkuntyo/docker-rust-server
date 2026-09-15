using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using RustMonitor.Core;

namespace RustMonitor;

public partial class MainWindow
{
    private readonly Func<SshProfile, CancellationToken, Task<ChatSnapshot>> readChat;
    private readonly Func<SshProfile, string, CancellationToken, Task<ChatSendResult>> sendChat;
    private readonly DispatcherTimer chatTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly Dictionary<string, string> chatDrafts = [];
    private ChatSnapshot chat = new();
    private bool chatReady, chatReading, chatSending, chatExpanded = true;
    private string chatRendered = "";

    private void InitializeChat()
    {
        ChatVisible.IsChecked = store.Get("chat-visible") != "false";
        chatExpanded = store.Get("chat-expanded") != "false";
        ChatGlobalOnly.IsChecked = store.Get("chat-global-only") != "false";
        chatReady = true;
        chatTimer.Tick += async (_, _) => await RefreshChatAsync();
        Loaded += (_, _) => StartChat();
        UpdateChatVisibility();
    }
    private void LoadChat(SshProfile next)
    {
        chat = store.Get("chat:" + next.Key) is string json ? Wire.Read<ChatSnapshot>(json) : new();
        ChatInput.Text = chatDrafts.GetValueOrDefault(next.Key, "");
        ChatSendStatus.Text = "送信ボタン / Ctrl＋Enter で発言";
        ChatStateText.Text = chat.CheckedAt.Length > 0 ? "保存済み • " + Time(chat.CheckedAt) : "接続すると取得します";
        chatRendered = ""; BindChat(); UpdateChatControls();
    }
    private void UpdateChatControls()
    {
        if (!chatReady) return;
        ChatInput.IsEnabled = live && profile != null && !chatSending;
        ChatSend.IsEnabled = ChatInput.IsEnabled && !string.IsNullOrWhiteSpace(ChatInput.Text);
        ChatDestination.Text = profile == null ? "サーバーへ接続してください" : "送信先：" + (server?.Name is { Length: > 0 } name ? name : profile.Container);
        ChatDestination.ToolTip = profile == null ? "" : profile.Target + " / " + profile.Container;
    }
    private void UpdateChatVisibility()
    {
        ChatOverlay.Visibility = ChatVisible.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ChatBody.Visibility = chatExpanded ? Visibility.Visible : Visibility.Collapsed;
        ChatFold.Content = (chatExpanded ? "▼" : "▶") + " サーバーチャット";
    }
    private void ChatVisibility_Changed(object sender, RoutedEventArgs e)
    {
        if (!chatReady) return;
        store.Put("chat-visible", ChatVisible.IsChecked == true ? "true" : "false");
        UpdateChatVisibility(); StartChat();
    }
    private void ChatHide_Click(object sender, RoutedEventArgs e) => ChatVisible.IsChecked = false;
    private void ChatFold_Click(object sender, RoutedEventArgs e)
    {
        chatExpanded = !chatExpanded; store.Put("chat-expanded", chatExpanded ? "true" : "false"); UpdateChatVisibility();
    }
    private void ChatFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (!chatReady) return;
        store.Put("chat-global-only", ChatGlobalOnly.IsChecked == true ? "true" : "false");
        chatRendered = ""; BindChat();
    }
    private void StartChat()
    {
        if (closed || !live || ChatVisible.IsChecked != true) { chatTimer.Stop(); return; }
        if (!IsLoaded) return;
        chatTimer.Start(); _ = RefreshChatAsync();
    }
    private async Task RefreshChatAsync()
    {
        if (closed || !live || profile == null || ChatVisible.IsChecked != true || chatReading) return;
        var next = profile; var current = generation;
        chatReading = true;
        try
        {
            var snapshot = await readChat(next, session.Token);
            if (closed || current != generation) return;
            chat = snapshot;
            store.Put("chat:" + next.Key, Wire.Write(snapshot));
            BindChat();
            ChatStateText.Text = "5秒ごとに更新 • 最新 " + chat.Messages.Count + " 件";
            ChatStateText.ToolTip = "最終取得 " + Time(chat.CheckedAt) + "。サーバーに残る最新200件までを表示します。";
        }
        catch (Exception ex)
        {
            if (!closed && current == generation)
            { ChatStateText.Text = "チャット取得待ち • 履歴を表示"; ChatStateText.ToolTip = SafeError(ex); }
        }
        finally { chatReading = false; }
    }
    private void BindChat()
    {
        var rows = chat.Messages.Where(m => ChatGlobalOnly.IsChecked != true || m.Channel is 0 or 2).ToList();
        var content = Wire.Write(rows);
        if (content == chatRendered) return;
        var followLatest = ChatScroll.ScrollableHeight - ChatScroll.VerticalOffset < 24 || chatRendered.Length == 0;
        chatRendered = content; ChatMessages.ItemsSource = rows;
        ChatEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (followLatest) ChatScroll.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => { if (!closed) ChatScroll.ScrollToEnd(); });
    }
    private void ChatInput_Changed(object sender, TextChangedEventArgs e) => UpdateChatControls();
    private async void ChatInput_KeyDown(object sender, KeyEventArgs e)
    {
        // Plain Enter and IME composition never send a message.
        if (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.Control) return;
        e.Handled = true; await SendChatAsync();
    }
    private async void ChatSend_Click(object sender, RoutedEventArgs e) => await SendChatAsync();
    private async Task SendChatAsync()
    {
        if (closed || !live || profile == null || chatSending) return;
        string message;
        try { message = ChatText.Validate(ChatInput.Text); }
        catch (ArgumentException ex) { ChatSendStatus.Text = ex.Message; return; }
        var destination = profile;
        chatSending = true; SetBusy(mainBusy); ChatSendStatus.Text = "送信しています…";
        try
        {
            // A roster refresh/stop cannot cancel an already dispatched broadcast.
            // There is no automatic retry when the acknowledgement is lost.
            var result = await sendChat(destination, message, CancellationToken.None);
            if (closed) return;
            if (result.Accepted)
            { ChatInput.Clear(); chatDrafts.Remove(destination.Key); ChatSendStatus.Text = "送信しました"; await RefreshChatAsync(); }
            else ChatSendStatus.Text = "送信結果を確認できません。再送する前にチャットを確認してください。";
        }
        catch (Exception)
        {
            if (!closed) ChatSendStatus.Text = "送信結果を確認できません。再送する前にチャットを確認してください。";
        }
        finally { chatSending = false; if (!closed) SetBusy(mainBusy); }
    }
}
