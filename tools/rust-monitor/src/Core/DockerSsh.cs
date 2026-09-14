using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace RustMonitor.Core;

public sealed class DockerReport
{
    public string CheckedAt { get; set; } = "";
    public List<DockerServer> Servers { get; set; } = [];
}

public sealed class DockerServer
{
    public string Container { get; set; } = "";
    public string Name { get; set; } = "";
    public int? RconPort { get; set; }
    public int? Current { get; set; }
    public int? SeasonUnique { get; set; }
    public int? Capacity { get; set; }
    public int? Peak { get; set; }
    public string PeakAt { get; set; } = "";
    public string PeakLastAt { get; set; } = "";
    public string SeasonStart { get; set; } = "";
    public string SeasonEnd { get; set; } = "";
    public string LogFirstAt { get; set; } = "";
    public string LogCutoff { get; set; } = "";
    public string CheckedAt { get; set; } = "";
    public int Samples { get; set; }
    public bool PartialLogs { get; set; }
    public bool PeakFromSaved { get; set; }
    public bool CapacityFromSaved { get; set; }
    public string RconStatus { get; set; } = "";
    public string HistoryStatus { get; set; } = "";
    public string LogError { get; set; } = "";
    public string Error { get; set; } = "";
    public string DisplayName => Name == Container ? Container : Name + " (" + Container + ")";
    private static string Local(string value, string format) => DateTimeOffset.TryParse(value, out var time) ? time.ToLocalTime().ToString(format) : "未確認";
    public string CurrentText => Current?.ToString() ?? (RconStatus == "connection_reset" ? "接続切断" : RconStatus is "timeout" or "unavailable" ? "応答待ち" : "未確認");
    public string RconPortText => RconPort?.ToString() ?? "未確認";
    public string SeasonUniqueText => SeasonUnique?.ToString() ?? (HistoryStatus == "missing" ? "履歴未作成" : HistoryStatus == "invalid" ? "読取待ち" : "未確認");
    public string CapacityText => Capacity == null ? "未確認" : Capacity + (CapacityFromSaved ? "（前回）" : "");
    public string PeakText => Peak == null ? (LogError.Length > 0 ? LogError : SeasonEnd.Length == 0 ? "期間未確認" : "記録なし") : $"{Peak} 人\n{Local(PeakAt, "MM/dd HH:mm:ss")}" + (PartialLogs ? "\n保存ログ内の最大" : "\n定期記録の最大") + (PeakFromSaved ? " / DB保持値" : "");
    public string SeasonText => $"{Local(SeasonStart, "yyyy/MM/dd HH:mm")}\n～ {Local(SeasonEnd, "yyyy/MM/dd HH:mm")}";
    public string DetailText => Error.Length != 0 ? Error : $"{Container} • 対象ログ {Samples} 件 • ログ開始 {Local(LogFirstAt, "yyyy/MM/dd HH:mm:ss")}";
}

public static class DockerSsh
{
    public static bool ValidTarget(string target) => Regex.IsMatch(target, @"\A(?:[A-Za-z0-9_][A-Za-z0-9_.-]*@)?[A-Za-z0-9][A-Za-z0-9.:-]*\z");
    public static bool ValidContainer(string container) => Regex.IsMatch(container, @"\Arust-[A-Za-z0-9][A-Za-z0-9_.-]*\z");
    public static Task<DockerReport> ReadOverviewAsync(string target, CancellationToken cancellation = default) => ReadAsync<DockerReport>(target, new { mode = "overview" }, cancellation);
    public static Task<DockerReport> ListServersAsync(string target, CancellationToken cancellation = default) => ReadAsync<DockerReport>(target, new { mode = "list" }, cancellation);
    public static Task<SshServerSnapshot> ReadServerAsync(SshProfile profile, CancellationToken cancellation = default)
    {
        if (!ValidContainer(profile.Container)) throw new ArgumentException("一覧から Rust コンテナを選択してください。");
        return ReadAsync<SshServerSnapshot>(profile.Target, new { mode = "server", container = profile.Container }, cancellation);
    }
    public static Task<SshMap> ReadMapAsync(SshProfile profile, string wipe, CancellationToken cancellation = default)
    {
        if (!ValidContainer(profile.Container)) throw new ArgumentException("一覧から Rust コンテナを選択してください。");
        return ReadAsync<SshMap>(profile.Target, new { mode = "map", container = profile.Container, wipe }, cancellation);
    }
    private static async Task<T> ReadAsync<T>(string target, object request, CancellationToken cancellation)
    {
        if (!ValidTarget(target)) throw new ArgumentException("SSH 接続先を user@hostname 形式で入力してください。");
        var source = Path.Combine(AppContext.BaseDirectory, "collector", "docker_status.py");
        var requestData = Convert.ToBase64String(Encoding.UTF8.GetBytes(Wire.Write(request)));
        var script = "import json, base64\nREQUEST = json.loads(base64.b64decode('" + requestData + "'))\n"
            + await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "collector", "save_reader.py"), cancellation)
            + "\n" + await File.ReadAllTextAsync(source, cancellation);
        var start = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "OpenSSH", "ssh.exe"),
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var arg in new[] { "-T", "-o", "BatchMode=yes", "-o", "ConnectTimeout=10", "-o", "StrictHostKeyChecking=yes", "--", target, "python3", "-" }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("Windows OpenSSH を起動できません。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(TimeSpan.FromSeconds(90));
        try
        {
            var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.StandardInput.WriteAsync(script.AsMemory(), timeout.Token); process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            var output = await outputTask;
            var error = await errorTask;
            if (process.ExitCode != 0) throw new IOException(error.Contains("Host key verification failed", StringComparison.OrdinalIgnoreCase)
                ? "SSH のホスト鍵が未登録、または変更されています。ターミナルから接続先を確認してください。"
                : "SSH で取得できません。鍵認証、Tailscale、sudo -n docker、python3 を確認してください。");
            Wire.Check(output);
            return Wire.Read<T>(output);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(true);
            throw new IOException("SSH の取得がキャンセルされたか、時間内に完了しませんでした。");
        }
    }
    public static void Remember(Store store, string target, DockerReport report)
    {
        var oldReport = store.Get("docker-report:" + target) is string oldJson ? Wire.Read<DockerReport>(oldJson) : null;
        foreach (var server in report.Servers)
        {
            var oldServer = oldReport?.Servers.FirstOrDefault(s => s.Container == server.Container);
            if (oldServer != null && server.Error.Length > 0)
            {
                if (server.Name == server.Container || server.Name.Length == 0) server.Name = oldServer.Name;
                if (server.Capacity == null && oldServer.Capacity != null) { server.Capacity = oldServer.Capacity; server.CapacityFromSaved = true; }
            }
            // Presence failures must not discard good historical data. An unknown/new
            // season must never inherit a previous season's counts or peak.
            if (server.SeasonEnd.Length == 0) continue;
            var key = "docker-peak:" + target + ":" + server.Container + ":" + server.SeasonEnd;
            var previousJson = store.Get(key);
            if (previousJson != null)
            {
                var previous = Wire.Read<DockerServer>(previousJson);
                if (previous.Peak.HasValue && (!server.Peak.HasValue || previous.Peak > server.Peak))
                { server.Peak = previous.Peak; server.PeakAt = previous.PeakAt; server.PeakLastAt = previous.PeakLastAt; server.PeakFromSaved = true; }
                else if (previous.Peak.HasValue && previous.Peak == server.Peak)
                {
                    if (DateTimeOffset.TryParse(previous.PeakAt, out var first) && (!DateTimeOffset.TryParse(server.PeakAt, out var currentFirst) || first < currentFirst))
                    { server.PeakAt = previous.PeakAt; server.PeakFromSaved = true; }
                    if (DateTimeOffset.TryParse(previous.PeakLastAt, out var last) && (!DateTimeOffset.TryParse(server.PeakLastAt, out var currentLast) || last > currentLast))
                        server.PeakLastAt = previous.PeakLastAt;
                }
            }
            store.Put(key, Wire.Write(server));
        }
        store.Put("docker-report:" + target, Wire.Write(report));
        store.Put("last-ssh-target", target);
    }
}
