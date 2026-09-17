using System.Collections.Specialized;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using RustMonitor;
using RustMonitor.Core;

internal static partial class Program
{
    private const string CombatSteamId = "76561198000000001";
    private static CombatRow Hit(double age, string hp = "75.0") => new() { AgeSeconds = age,
        Fields = ["you", "123", "player", "456", "assets/prefabs/weapons/ak47u/ak47u.entity.prefab", "riflebullet", "chest", "35.2m", "100.0", hp, "", "1", "1.00", "0.12s", "0.00m", "35"] };
    private static CombatSnapshot CombatSample(double sample, params CombatRow[] rows) => new()
    {
        SteamId = CombatSteamId, Epoch = "epoch-1", CheckedAt = new DateTimeOffset(2026, 9, 17, 0, 0, 0, TimeSpan.Zero).AddSeconds(sample).ToString("O"),
        SampleSeconds = sample, UncertaintySeconds = .3, Available = true, Rows = rows.ToList()
    };
    private static void CombatChecks()
    {
        var history = new CombatHistory();
        Check(history.Append(CombatSample(100, Hit(30), Hit(20)), CombatSteamId).Added.Count == 2, "combat first read retains even identical hits as separate events");
        var originalTime = history.Entries[0].OccurredAt;
        Check(history.Append(CombatSample(160.1, Hit(90), Hit(80)), CombatSteamId).Added.Count == 0 && history.Entries[0].OccurredAt == originalTime,
            "elapsed ages and normal request jitter do not duplicate old combat events or change their displayed times");
        Check(history.Append(CombatSample(220, Hit(140), Hit(10)), CombatSteamId).Added.Count == 1 && history.Entries.Count == 3,
            "a rolling server buffer appends a new identical hit without duplicating its overlap");
        var restored = Wire.Read<CombatHistory>(Wire.Write(history));
        Check(restored.Append(CombatSample(280, Hit(200), Hit(70)), CombatSteamId).Added.Count == 0, "saved combat history resumes without re-adding the current buffer");
        restored.Append(CombatSample(300), CombatSteamId);
        Check(restored.Append(CombatSample(340, Hit(260), Hit(130)), CombatSteamId).Added.Count == 0, "temporary empty responses do not discard the combat deduplication cursor");
        var absent = CombatSample(350); absent.Available = false; absent.Reason = "対象なし";
        Check(restored.Append(absent, CombatSteamId).Notice == "対象なし" && restored.Entries.Count == 3, "an unavailable sleeper keeps existing combat history");
        var restart = CombatSample(360, Hit(10)); restart.Epoch = "epoch-2";
        Check(restored.Append(restart, CombatSteamId).Added.Count == 1 && restored.Entries.Count == 4, "server restart accepts new events while retaining the earlier history");
        var gap = CombatSample(500, Hit(5, "0.0")); gap.Epoch = "epoch-2";
        Check(restored.Append(gap, CombatSteamId).Notice.Contains("欠け"), "a completely replaced combat buffer explains that intervening events may be missing");
        var duplicate = new CombatHistory(); duplicate.Append(CombatSample(100, Hit(10), Hit(10)), CombatSteamId);
        Check(duplicate.Append(CombatSample(160, Hit(70), Hit(70), Hit(5)), CombatSteamId).Added.Count == 1, "same-time repeated hit multiplicity is preserved across polls");
        try { restored.Append(CombatSample(550, Hit(10)), "76561198000000002"); Check(false, "wrong combat target"); }
        catch (InvalidDataException) { Check(true, "a response for another player is rejected before appending"); }
        CombatWindowChecks();
    }
    private static void CombatWindowChecks()
    {
        using var db = new Store(Path.Combine(root, "combat-fixture.sqlite3"));
        var profile = new SshProfile("admin@demo.invalid", "rust-demo");
        var snapshot = CombatSample(100, Hit(30), Hit(20));
        var requests = new List<(SshProfile, string)>(); var fail = false;
        var window = new CombatLogWindow(db, profile, CombatSteamId, "Demo Player A", "DEMO / Combat log", (p, id, _) =>
        { requests.Add((p, id)); return fail ? Task.FromException<CombatSnapshot>(new IOException("test unavailable")) : Task.FromResult(snapshot); });
        var timer = (DispatcherTimer)typeof(CombatLogWindow).GetField("timer", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(window)!;
        var grid = (DataGrid)window.FindName("LogGrid");
        var changes = new List<NotifyCollectionChangedAction>();
        ((INotifyCollectionChanged)grid.ItemsSource).CollectionChanged += (_, e) => changes.Add(e.Action);
        window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        Check(timer.Interval == TimeSpan.FromMinutes(1) && timer.IsEnabled && grid.Items.Count == 2 && requests.All(r => r == (profile, CombatSteamId)),
            "opening the combat window fetches the fixed player and starts its one-minute timer");
        changes.Clear(); snapshot = CombatSample(160, Hit(90), Hit(80), Hit(5, "0.0")); window.RefreshAsync().GetAwaiter().GetResult();
        Check(changes.SequenceEqual(new[] { NotifyCollectionChangedAction.Add }) && grid.Items.Count == 3,
            "combat refresh adds only the new row without resetting the existing table");
        fail = true; window.RefreshAsync().GetAwaiter().GetResult();
        Check(grid.Items.Count == 3 && ((TextBox)window.FindName("StatusText")).Text.Contains("更新できません"), "failed combat polling keeps already displayed rows");
        fail = false; window.RefreshAsync().GetAwaiter().GetResult();
        Check(grid.Items.Count == 3, "recovery after a failed poll does not duplicate combat rows");
        var content = (FrameworkElement)window.Content;
        content.Measure(new Size(1332, 688)); content.Arrange(new Rect(0, 0, 1332, 688)); content.UpdateLayout();
        SaveRender(content, "combat-preview.png", 1332, 688);
        var copy = VisualChildren<RustMonitor.Controls.SelectableText>(grid).First(t => t.Text == "100.0 → 75.0");
        copy.SelectAll(); Check(CaptureCopy(copy) == "100.0 → 75.0", "combat table values remain selectable and copyable");
        ((Button)window.FindName("PauseButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(!timer.IsEnabled && ((TextBox)window.FindName("CountText")).Text.Contains("停止"), "combat automatic polling can be paused");
        ((Button)window.FindName("PauseButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(timer.IsEnabled, "resuming combat polling restarts the one-minute timer");
        window.Close();
        var reopened = new CombatLogWindow(db, profile, CombatSteamId, "Demo", "DEMO", (_, _, _) => Task.FromResult(snapshot));
        reopened.RefreshAsync().GetAwaiter().GetResult();
        Check(((DataGrid)reopened.FindName("LogGrid")).Items.Count == 3 &&
            db.Get(CombatHistory.Key(profile with { Container = "rust-other" }, CombatSteamId)) == null &&
            db.Get(CombatHistory.Key(profile, "76561198000000002")) == null, "combat history persists and stays isolated by server and player");
        reopened.Close();
        var pending = new TaskCompletionSource<CombatSnapshot>(); CancellationToken token = default; var calls = 0;
        var closing = new CombatLogWindow(db, profile, "76561198000000002", "Closing", "DEMO", (_, _, ct) => { calls++; token = ct; return pending.Task; });
        var fetching = closing.RefreshAsync(); closing.RefreshAsync().GetAwaiter().GetResult();
        Check(calls == 1, "a slow combat poll cannot overlap another manual or timed read");
        closing.Close(); pending.SetResult(snapshot); fetching.GetAwaiter().GetResult();
        Check(token.IsCancellationRequested && db.Get(CombatHistory.Key(profile, "76561198000000002")) == null,
            "closing the combat window cancels its read and ignores a late response");
    }
}
