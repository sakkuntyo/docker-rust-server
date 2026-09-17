using System.Windows;
using RustMonitor.Core;

namespace RustMonitor;

public partial class MainWindow
{
    private readonly Func<SshProfile, CancellationToken, Task<ChatSnapshot>> readChat;
    private readonly Func<SshProfile, string, CancellationToken, Task<ChatSendResult>> sendChat;
    private readonly Dictionary<string, ChatWindow> chatWindows = [];
    private readonly Dictionary<string, string> chatDrafts = [];
    private void Chat_Click(object sender, RoutedEventArgs e)
    {
        if (profile == null) return;
        var window = GetChatWindow(profile, server?.Name is { Length: > 0 } name ? name : profile.Container);
        if (!window.IsVisible) { window.Owner = this; window.Show(); }
        else { if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal; window.Activate(); }
    }
    private ChatWindow GetChatWindow(SshProfile target, string name)
    {
        if (chatWindows.TryGetValue(target.Key, out var existing))
            return existing;
        var window = new ChatWindow(store, target, name, chatDrafts.GetValueOrDefault(target.Key, ""), readChat, sendChat);
        chatWindows.Add(target.Key, window);
        window.Closed += (_, _) => { chatDrafts[target.Key] = window.Draft; chatWindows.Remove(target.Key); };
        return window;
    }
}
