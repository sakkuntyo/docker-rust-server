using System.Diagnostics;
using System.IO;
using System.Net;
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
    public static async Task<SshServerSnapshot> ReadServerAsync(SshProfile profile, CancellationToken cancellation = default)
    {
        if (!ValidContainer(profile.Container)) throw new ArgumentException("一覧から Rust コンテナを選択してください。");
        var snapshot = await ReadAsync<SshServerSnapshot>(profile.Target, new { mode = "server", container = profile.Container }, cancellation);
        var online = snapshot.Players.Where(p => p.Online).ToList();
        if (!snapshot.PresenceAvailable || online.Count == 0) return snapshot;
        try
        {
            var route = snapshot.IpRoute;
            if (route.Reason.Length > 0) throw new IOException(route.Reason);
            if (Uri.CheckHostName(route.Host) != UriHostNameType.Dns || !Regex.IsMatch(route.User, @"\A[A-Za-z0-9_][A-Za-z0-9_.-]*\z") ||
                route.GamePort is < 1 or > 65535 || route.ServerIps.Count is < 1 or > 8 || route.ServerIps.Any(ip => !IPAddress.TryParse(ip, out _)))
                throw new IOException("中継サーバーの接続情報を確認できません");
            var validPeers = online.Where(p => IPEndPoint.TryParse(p.Address, out var peer) && route.GatewayIps.Contains(peer.Address.ToString())).Select(p => p.Address).Distinct().ToArray();
            if (validPeers.Length == 0) throw new IOException("プレイヤーの接続先と使用中の中継サーバーが一致しません");
            using var lookup = CancellationTokenSource.CreateLinkedTokenSource(cancellation); lookup.CancelAfter(TimeSpan.FromSeconds(30));
            var connections = await ReadAsync<ConnectionReport>(route.User + "@" + route.Host,
                new { mode = "connections", gamePort = route.GamePort, serverIps = route.ServerIps, peers = validPeers }, lookup.Token);
            if (connections.Status != "ok") throw new IOException(connections.Status == "tool_missing" ? "中継サーバーに conntrack がありません" : "中継サーバーの conntrack を読み取れません");
            var confirmation = await ReadAsync<PresenceSnapshot>(profile.Target, new { mode = "presence", container = profile.Container }, lookup.Token);
            IpAttribution.Apply(snapshot, connections, confirmation);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or OperationCanceledException)
        {
            cancellation.ThrowIfCancellationRequested();
            foreach (var player in online) { player.IpVerified = false; player.IpReason = ex is OperationCanceledException ? "IP の照合が時間内に完了しませんでした" : ex.Message; }
            snapshot.Warning = string.Join(" / ", new[] { snapshot.Warning, "本IPの照合待ち（メンバーのIP欄で詳細を確認）" }.Where(s => s.Length > 0));
        }
        return snapshot;
    }
    public static Task<SshMap> ReadMapAsync(SshProfile profile, string wipe, CancellationToken cancellation = default)
    {
        if (!ValidContainer(profile.Container)) throw new ArgumentException("一覧から Rust コンテナを選択してください。");
        return ReadAsync<SshMap>(profile.Target, new { mode = "map", container = profile.Container, wipe }, cancellation);
    }
    public static Task<ChatSnapshot> ReadChatAsync(SshProfile profile, CancellationToken cancellation = default)
    {
        if (!ValidContainer(profile.Container)) throw new ArgumentException("Rust コンテナを選択してください。");
        return ReadAsync<ChatSnapshot>(profile.Target, new { mode = "chat", container = profile.Container }, cancellation, "chat.py", 20);
    }
    public static Task<CombatSnapshot> ReadCombatAsync(SshProfile profile, string steamId, CancellationToken cancellation = default)
    {
        if (!ValidContainer(profile.Container) || !Regex.IsMatch(steamId, @"\A[0-9]{17}\z"))
            throw new ArgumentException("サーバーとプレイヤーを選択してください。");
        return ReadAsync<CombatSnapshot>(profile.Target, new { mode = "combat", container = profile.Container, steamid = steamId }, cancellation, "combat.py", 40);
    }
    public static Task<ChatSendResult> SendChatAsync(SshProfile profile, string message, CancellationToken cancellation = default)
    {
        if (!ValidContainer(profile.Container)) throw new ArgumentException("Rust コンテナを選択してください。");
        message = ChatText.Validate(message);
        return ReadAsync<ChatSendResult>(profile.Target, new { mode = "say", container = profile.Container, message }, cancellation, "chat.py", 20);
    }
    private static async Task<T> ReadAsync<T>(string target, object request, CancellationToken cancellation, string scriptFile = "docker_status.py", int timeoutSeconds = 90)
    {
        if (!ValidTarget(target)) throw new ArgumentException("SSH 接続先を user@hostname 形式で入力してください。");
        var source = Path.Combine(AppContext.BaseDirectory, "collector", scriptFile);
        var requestData = Convert.ToBase64String(Encoding.UTF8.GetBytes(Wire.Write(request)));
        var script = "import json, base64\nREQUEST = json.loads(base64.b64decode('" + requestData + "'))\n";
        if (scriptFile == "docker_status.py")
            script += await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "collector", "save_reader.py"), cancellation)
                + "\n" + await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "collector", "conntrack_reader.py"), cancellation) + "\n";
        script += await File.ReadAllTextAsync(source, cancellation);
        var start = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "OpenSSH", "ssh.exe"),
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var arg in new[] { "-T", "-o", "BatchMode=yes", "-o", "ConnectTimeout=10", "-o", "StrictHostKeyChecking=yes", "--", target, "python3", "-" }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("Windows OpenSSH を起動できません。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
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
