using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using RustMonitor.Core;

namespace RustMonitor;

public partial class MainWindow
{
    private readonly Func<SshProfile, string, CancellationToken, Task<PlayerTeam>> readTeam;
    private CancellationTokenSource? teamCancellation;
    private string teamKey = "", teamError = "";
    private PlayerTeam? displayedTeam;
    private DateTimeOffset teamAttempt;
    private bool teamBusy;
    private int teamGeneration;
    private string SelectedTeamKey => profile != null && PlayerList.SelectedItem is PlayerRow row
        ? profile.Key + ":" + server?.WipeId + ":" + row.SteamId : "";
    private void UpdateTeamSelection()
    {
        if (closed) return;
        var key = SelectedTeamKey;
        if (key != teamKey)
        {
            teamGeneration++; teamCancellation?.Cancel(); teamCancellation?.Dispose(); teamCancellation = null;
            teamKey = key; teamBusy = false; teamAttempt = default; teamError = ""; displayedTeam = null;
            if (key.Length > 0 && store.Get("team:" + key) is string cached)
                try { var saved = Wire.Read<PlayerTeam>(cached); saved.Validate(((PlayerRow)PlayerList.SelectedItem).SteamId); displayedTeam = saved; }
                catch (Exception) { }
        }
        DrawTeam();
        if (IsLoaded && live && inventoryDetailsVisible && key.Length > 0 && !teamBusy && DateTimeOffset.UtcNow - teamAttempt >= TimeSpan.FromSeconds(30))
            _ = RefreshTeamAsync();
    }
    private void DrawTeam()
    {
        TeamRefreshButton.IsEnabled = teamKey.Length > 0 && !teamBusy && !closed;
        TeamRefreshButton.Content = teamBusy ? "…" : "↻";
        TeamHeading.Text = displayedTeam?.Heading ?? "パーティ：未確認";
        DrawTeamMembers();
        TeamMembersScroll.Visibility = displayedTeam?.Members.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        TeamStatus.Text = (displayedTeam == null ? "" : "確認 " + Time(displayedTeam.CheckedAt) + "\n") +
            (teamBusy ? "取得中…" : teamError.Length > 0 ? teamError + (displayedTeam == null ? "" : "（前回の記録を表示）") :
                teamKey.Length == 0 ? "メンバーを選択してください。" : live ? "" : "更新停止中 • ↻で取得できます。");
    }
    private void DrawTeamMembers()
    {
        var text = displayedTeam?.MemberText ?? "";
        // Keep the current selection when a refresh leaves the member list unchanged.
        if (TeamMembers.Tag is string previous && previous == text) return;
        TeamMembers.Tag = text;
        var paragraph = new Paragraph { Margin = new Thickness(0) };
        foreach (var member in (displayedTeam?.Members ?? []).OrderByDescending(m => m.Leader))
        {
            if (paragraph.Inlines.Count > 0) paragraph.Inlines.Add(new LineBreak());
            paragraph.Inlines.Add(new Run(member.Label + " • "));
            paragraph.Inlines.Add(new Run(member.Online ? "オンライン" : "オフライン")
            {
                Foreground = member.Online ? new SolidColorBrush(Color.FromRgb(125, 222, 161)) : TeamMembers.Foreground,
                FontWeight = member.Online ? FontWeights.SemiBold : FontWeights.Normal
            });
            paragraph.Inlines.Add(new LineBreak());
            paragraph.Inlines.Add(new Run("Steam ID: " + member.SteamId));
        }
        TeamMembers.Document = new FlowDocument(paragraph) { PagePadding = new Thickness(0),
            FontFamily = TeamMembers.FontFamily, FontSize = TeamMembers.FontSize, Foreground = TeamMembers.Foreground };
    }
    private void TeamMembers_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        TeamMembersScroll.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = MouseWheelEvent });
    }
    private async void TeamRefresh_Click(object sender, RoutedEventArgs e) => await RefreshTeamAsync();
    public async Task RefreshTeamAsync()
    {
        if (closed || teamBusy || profile == null || PlayerList.SelectedItem is not PlayerRow row || teamKey != SelectedTeamKey || teamKey.Length == 0) return;
        var target = profile; var key = teamKey; var current = teamGeneration; var connection = generation;
        teamCancellation?.Dispose(); teamCancellation = CancellationTokenSource.CreateLinkedTokenSource(session.Token);
        var token = teamCancellation.Token;
        teamBusy = true; teamAttempt = DateTimeOffset.UtcNow; teamError = ""; DrawTeam();
        bool Current() => !closed && !token.IsCancellationRequested && teamGeneration == current && generation == connection && key == SelectedTeamKey;
        try
        {
            var result = await readTeam(target, row.SteamId, token);
            if (!Current()) return;
            result.Validate(row.SteamId);
            displayedTeam = result; store.Put("team:" + key, Wire.Write(result));
        }
        catch (Exception ex) { if (Current()) teamError = SafeError(ex); }
        finally { if (!closed && teamGeneration == current) { teamBusy = false; DrawTeam(); } }
    }
}
