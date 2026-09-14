using System.Net.WebSockets;
using System.IO;
using System.Text;
using System.Text.Json;

namespace RustMonitor.Core;

public sealed class CollectorUnavailableException() : IOException("RCON 接続は確認できました。RustMonitorCollector が未導入または未ロードです。サーバーへ導入・ロードしてから再接続してください。");

public sealed class RconClient : IDisposable
{
    private readonly ClientWebSocket socket = new();
    private readonly SemaphoreSlim gate = new(1, 1);
    private int sequence;
    public bool Connected => socket.State == WebSocketState.Open;
    public async Task ConnectAsync(ConnectionProfile profile, string password)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        try { await socket.ConnectAsync(profile.Uri(password), timeout.Token); }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException)
        { socket.Abort(); throw new IOException("RCON に接続できません。接続先・パスワード・Tailscale 接続・rcon.web を確認してください。"); }
    }
    public async Task EnsureCollectorAsync()
    {
        // Rust returns no reply for an unregistered plugin command. The built-in
        // command lookup responds even when the collector has not been installed.
        var commands = await ReadMessageAsync("find rustmonitor.server");
        if (!commands.Contains("rustmonitor.server", StringComparison.OrdinalIgnoreCase))
            throw new CollectorUnavailableException();
    }
    public async Task<T> ReadAsync<T>(string command)
    {
        if (!command.StartsWith("rustmonitor.", StringComparison.Ordinal)) throw new ArgumentException("Unsupported command");
        var json = await ReadMessageAsync(command);
        if (!json.TrimStart().StartsWith('{')) throw new InvalidDataException("RustMonitorCollector プラグインが応答していません。サーバーへの導入とコンパイル結果を確認してください。");
        Wire.Check(json);
        return Wire.Read<T>(json);
    }
    private async Task<string> ReadMessageAsync(string command)
    {
        // The UI cannot issue arbitrary game commands.
        if (command != "find rustmonitor.server" && !command.StartsWith("rustmonitor.", StringComparison.Ordinal)) throw new ArgumentException("Unsupported command");
        await gate.WaitAsync();
        try
        {
            if (!Connected) throw new IOException("RCON が切断されています。");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var id = Interlocked.Increment(ref sequence);
            var request = Encoding.UTF8.GetBytes(Wire.Write(new { Identifier = id, Message = command, Name = "RustMonitor" }));
            await socket.SendAsync(new ArraySegment<byte>(request), WebSocketMessageType.Text, true, timeout.Token);
            var buffer = new byte[16384];
            while (true)
            {
                using var stream = new MemoryStream();
                WebSocketReceiveResult received;
                do
                {
                    received = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token);
                    if (received.MessageType == WebSocketMessageType.Close) throw new IOException("サーバーが RCON 接続を閉じました。");
                    stream.Write(buffer, 0, received.Count);
                    if (stream.Length > 6 * 1024 * 1024) throw new InvalidDataException("RCON 応答が大きすぎます。");
                } while (!received.EndOfMessage);
                using var message = JsonDocument.Parse(stream.ToArray());
                if (!message.RootElement.TryGetProperty("Identifier", out var identifier) || identifier.GetInt32() != id) continue;
                return message.RootElement.GetProperty("Message").GetString() ?? "";
            }
        }
        catch (OperationCanceledException)
        { socket.Abort(); throw new IOException(command.StartsWith("rustmonitor.", StringComparison.Ordinal)
            ? "収集プラグインの応答が時間内に返りませんでした。サーバーのプラグイン状態を確認してください。"
            : "RCON コマンドの応答が時間内に返りませんでした。サーバーの状態を確認してください。"); }
        catch (WebSocketException)
        { socket.Abort(); throw new IOException("RCON 接続が切断されました。再接続してください。"); }
        finally { gate.Release(); }
    }
    public void Dispose() { socket.Abort(); socket.Dispose(); }
}
