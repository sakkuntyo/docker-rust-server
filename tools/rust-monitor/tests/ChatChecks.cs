using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using RustMonitor;
using RustMonitor.Core;

internal static partial class Program
{
    private static void ChatChecks()
    {
        var fixture = Path.Combine(root, "chat-windows"); Directory.CreateDirectory(fixture);
        var first = new SshProfile("admin@first.invalid", "rust-demo");
        var second = new SshProfile("admin@second.invalid", "rust-demo");
        var now = DateTimeOffset.UtcNow;
        var snapshot = new ChatSnapshot { CheckedAt = now.ToString("O"), Messages = [
            new() { Channel = 0, Time = now.ToUnixTimeSeconds(), Username = "日本語の名前", Message = "こんにちは 🦀 <b>文字として表示</b>" },
            new() { Channel = 1, Time = now.ToUnixTimeSeconds(), Username = "Team", Message = "チームの発言" },
            new() { Channel = 2, Time = now.ToUnixTimeSeconds(), Username = "SERVER", Message = "サーバーからのお知らせ" }] };
        using (var db = new Store(Path.Combine(fixture, "monitor.sqlite3")))
        { db.Put("last-ssh-profile", Wire.Write(first)); db.Put("chat:" + first.Key, Wire.Write(snapshot)); }
        var reads = new List<SshProfile>(); var sends = new List<(SshProfile, string)>(); var readFail = false;
        var pending = new TaskCompletionSource<ChatSendResult>();
        var main = new MainWindow(fixture, chatReader: (p, _) => { reads.Add(p); return readFail ? Task.FromException<ChatSnapshot>(new IOException("test failure")) : Task.FromResult(snapshot); },
            chatSender: (p, message, _) => { sends.Add((p, message)); return pending.Task; });
        ChatWindow Open(SshProfile p) => (ChatWindow)typeof(MainWindow).GetMethod("GetChatWindow", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, [p, p == first ? "DEMO / First server" : "DEMO / Second server"])!;
        var window = Open(first); var input = (TextBox)window.FindName("ChatInput"); var send = (Button)window.FindName("ChatSend");
        var timer = (DispatcherTimer)typeof(ChatWindow).GetField("timer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        var messages = (ItemsControl)window.FindName("ChatMessages");
        input.Text = "テスト発言"; window.SendAsync().GetAwaiter().GetResult();
        Check(!send.IsEnabled && sends.Count == 0 && messages.Items.Count == 2, "cached chat is displayed in its own window but cannot send before a successful read");
        Check(ReferenceEquals(window, Open(first)) && main.FindName("ChatOverlay") == null, "chat reuses one window per server and no longer overlays the map");
        window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        Check(timer.Interval == TimeSpan.FromSeconds(5) && timer.IsEnabled && reads.SequenceEqual(new[] { first }) && send.IsEnabled,
            "opening chat reads the fixed server and starts its independent five-second timer");
        Check(messages.Items.Cast<ChatMessage>().All(m => m.Channel is 0 or 2), "global/server filter separates team messages");
        ((CheckBox)window.FindName("ChatGlobalOnly")).IsChecked = false;
        window.RefreshAsync().GetAwaiter().GetResult();
        Check(messages.Items.Count == 3, "chat polling replaces the tail without duplicating the same messages");
        var secondWindow = Open(second); ((TextBox)secondWindow.FindName("ChatInput")).Text = "別サーバーの下書き";
        typeof(MainWindow).GetMethod("LoadProfile", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, [second]);
        var sending = window.SendAsync(); window.SendAsync().GetAwaiter().GetResult(); window.Close(); main.Close();
        Check(sends.SequenceEqual(new[] { (first, "テスト発言") }) && window.IsSending && !send.IsEnabled && !input.IsEnabled,
            "switching the main server cannot redirect an existing chat send and pending sends block duplicates and closing");
        pending.SetResult(new() { Accepted = false }); sending.GetAwaiter().GetResult();
        Check(window.Draft == "テスト発言" && sends.Count == 1 && ((TextBox)window.FindName("ChatSendStatus")).Text.Contains("再送する前"),
            "unknown chat send outcomes retain the draft and never auto-retry");
        pending = new TaskCompletionSource<ChatSendResult>(); pending.SetResult(new() { Accepted = true });
        window.SendAsync().GetAwaiter().GetResult();
        Check(sends.Count == 2 && input.Text == "" && ((TextBox)window.FindName("ChatSendStatus")).Text == "送信しました", "acknowledged explicit chat sends clear only their own draft");
        input.Text = "次の下書き"; readFail = true; window.RefreshAsync().GetAwaiter().GetResult();
        Check(messages.Items.Count == 3 && !send.IsEnabled && window.Draft == "次の下書き", "read failure preserves chat history and draft but disables sending");
        readFail = false; window.RefreshAsync().GetAwaiter().GetResult();
        Check(send.IsEnabled, "a successful chat read restores sending");
        var pause = (Button)window.FindName("PauseButton"); pause.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(!timer.IsEnabled && !send.IsEnabled, "chat can pause its own polling and sending independently of the main window");
        pause.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(timer.IsEnabled && send.IsEnabled, "resuming chat polling rechecks the fixed destination before sending");
        var content = (FrameworkElement)window.Content;
        content.Measure(new Size(812, 608)); content.Arrange(new Rect(0, 0, 812, 608)); content.UpdateLayout();
        var copy = VisualChildren<RustMonitor.Controls.SelectableText>(messages).First(t => t.Text == snapshot.Messages[0].Message);
        copy.SelectAll(); Check(CaptureCopy(copy) == snapshot.Messages[0].Message, "separate chat window retains selectable and copyable messages");
        SaveRender(content, "chat-window-preview.png", 812, 608);
        content.Measure(new Size(632, 428)); content.Arrange(new Rect(0, 0, 632, 428)); content.UpdateLayout();
        SaveRender(content, "chat-window-small.png", 632, 428);
        var before = sends.Count; input.Text = "test\nquit"; window.SendAsync().GetAwaiter().GetResult();
        Check(sends.Count == before, "chat rejects control characters before dispatch");
        input.Text = "次の下書き"; window.Close();
        Check(!timer.IsEnabled && Open(first).Draft == "次の下書き" && secondWindow.Draft == "別サーバーの下書き",
            "closing stops chat polling and reopening preserves drafts separately for each server");
        var reopen = Open(first); var wasClosed = false; reopen.Closed += (_, _) => wasClosed = true; main.Close();
        Check(wasClosed, "closing the main window closes its chat windows before disposing storage");
        using var lateDb = new Store(Path.Combine(fixture, "late-chat.sqlite3"));
        var late = new TaskCompletionSource<ChatSnapshot>(); var calls = 0; CancellationToken token = default;
        var closing = new ChatWindow(lateDb, first, "DEMO", reader: (_, ct) => { calls++; token = ct; return late.Task; });
        var fetching = closing.RefreshAsync(); closing.RefreshAsync().GetAwaiter().GetResult(); closing.Close(); late.SetResult(snapshot); fetching.GetAwaiter().GetResult();
        Check(calls == 1 && token.IsCancellationRequested && lateDb.Get("chat:" + first.Key) == null,
            "chat prevents overlapping reads and ignores responses arriving after the window closes");
    }
}
