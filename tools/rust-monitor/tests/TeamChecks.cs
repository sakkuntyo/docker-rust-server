using System.IO;
using System.Windows;
using System.Windows.Controls;
using RustMonitor;
using RustMonitor.Core;

internal static partial class Program
{
    private static void TeamChecks()
    {
        var fixture = Path.Combine(root, "team-ui"); Directory.CreateDirectory(fixture);
        var target = new SshProfile("admin@demo.invalid", "rust-demo");
        var other = "76561198000000002";
        var team = new PlayerTeam { SteamId = CombatSteamId, State = "members", CheckedAt = DateTimeOffset.UtcNow.ToString("o"), Members = [
            new TeamMember { SteamId = CombatSteamId, Name = "Demo leader", Online = true, Leader = true },
            new TeamMember { SteamId = other, Name = "Demo member", Online = false }] };
        team.Validate(CombatSteamId);
        Check(team.MemberText.Contains("リーダー") && team.MemberText.Contains("オフライン") && team.MemberText.Contains(other), "party text shows leader, offline members and copyable Steam IDs");
        using (var db = new Store(Path.Combine(fixture, "monitor.sqlite3")))
        {
            db.Put("last-ssh-profile", Wire.Write(target));
            db.SaveRoster(target.Key, new ServerState { Name = "Demo server", WipeId = "save:1:demo" }, [
                new PlayerRecord { SteamId = CombatSteamId, Name = "A leader" }, new PlayerRecord { SteamId = other, Name = "B member" }]);
        }
        var pending = new TaskCompletionSource<PlayerTeam>(); var reads = 0;
        using var icons = PrepareRenderIcons(fixture);
        var main = new MainWindow(fixture, itemIcons: icons, teamReader: (profile, id, _) =>
        { Check(profile == target && id == ((MainWindow.PlayerRow)((ListBox)currentMain!.FindName("PlayerList")).SelectedItem).SteamId, "party lookup targets the selected player and server"); reads++; return pending.Task; });
        currentMain = main;
        var list = (ListBox)main.FindName("PlayerList"); list.SelectedIndex = 0;
        var first = main.RefreshTeamAsync(); main.RefreshTeamAsync().GetAwaiter().GetResult();
        Check(reads == 1, "party refresh does not overlap");
        pending.SetResult(team); first.GetAwaiter().GetResult();
        Check(((TextBox)main.FindName("TeamHeading")).Text.Contains("2 人") && TeamText(main).Contains(other), "selected player's information displays every party member");
        var content = (FrameworkElement)main.Content; content.Measure(new Size(1550, 920)); content.Arrange(new Rect(0, 0, 1550, 920)); content.UpdateLayout();
        SaveRender(content, "party-preview.png", 1550, 920);
        pending = new(); first = main.RefreshTeamAsync(); pending.SetException(new IOException("Example connection failure")); first.GetAwaiter().GetResult();
        Check(((TextBox)main.FindName("TeamStatus")).Text.Contains("前回") && TeamText(main).Contains(other), "party failure keeps previous members and labels the stale record");
        ((TextBox)main.FindName("SearchBox")).Text = "A leader";
        var document = ((RichTextBox)main.FindName("TeamMembers")).Document;
        var link = document.Blocks.OfType<System.Windows.Documents.Paragraph>().Single().Inlines.OfType<System.Windows.Documents.Hyperlink>().Single(l => (string)l.Tag == other);
        pending = new(); first = main.RefreshTeamAsync();
        link.RaiseEvent(new RoutedEventArgs(System.Windows.Documents.Hyperlink.ClickEvent));
        Check(((MainWindow.PlayerRow)list.SelectedItem).SteamId == other && ((TextBox)main.FindName("SearchBox")).Text == "" && ((TextBox)main.FindName("InventoryName")).Text == "B member",
            "clicking a party Steam ID reveals a filtered-out member and updates the selected player's details");
        pending.SetResult(team); first.GetAwaiter().GetResult();
        Check(TeamText(main) == "", "late party result cannot appear under another selected player");
        pending = new(); first = main.RefreshTeamAsync(); pending.SetResult(new PlayerTeam { SteamId = other, State = "none", CheckedAt = team.CheckedAt }); first.GetAwaiter().GetResult();
        Check(((TextBox)main.FindName("TeamHeading")).Text.Contains("所属なし"), "no party is displayed separately from a lookup failure");
        list.SelectedIndex = 0;
        Check(TeamText(main).Contains(other), "cached party information returns for its original player only");
        pending = new(); first = main.RefreshTeamAsync(); main.Close(); pending.SetResult(team); first.GetAwaiter().GetResult();
        Check(first.IsCompletedSuccessfully, "closing the main window ignores a late party reply");
        currentMain = null;
    }
    private static string TeamText(MainWindow main) { var box = (RichTextBox)main.FindName("TeamMembers"); return new System.Windows.Documents.TextRange(box.Document.ContentStart, box.Document.ContentEnd).Text.TrimEnd(); }
    private static MainWindow? currentMain;
}
