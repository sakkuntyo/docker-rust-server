using System.Text.Json;
using System.IO;
using System.Text.Json.Serialization;

namespace RustMonitor.Core;

public static class Wire
{
    public static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    public static T Read<T>(string json) => JsonSerializer.Deserialize<T>(json, Json)
        ?? throw new InvalidDataException("サーバーから空のデータが返されました。");
    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Json);
    public static void Check(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("Error", out var error))
            throw new InvalidDataException(error.GetString());
    }
}

public sealed class ServerState
{
    public int Protocol { get; set; }
    public string Name { get; set; } = "";
    public string WipeId { get; set; } = "";
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public long Seed { get; set; }
    public int Size { get; set; }
    public string CapturedAt { get; set; } = "";
    public string CollectorStartedAt { get; set; } = "";
    public bool MapAvailable { get; set; }
    public string SaveAt { get; set; } = "";
}

public sealed class PlayerRecord
{
    public string SteamId { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Online { get; set; }
    public bool BodyAvailable { get; set; }
    public string FirstSeen { get; set; } = "";
    public string LastSeen { get; set; } = "";
    public string LastConnected { get; set; } = "";
    public string LastDisconnected { get; set; } = "";
    public string ObservedAt { get; set; } = "";
    public string WipeId { get; set; } = "";
    public double? X { get; set; }
    public double? Y { get; set; }
    public double? Z { get; set; }
    public string PositionAt { get; set; } = "";
    public string PositionReason { get; set; } = "";
}

public sealed class PlayerPage
{
    public string WipeId { get; set; } = "";
    public int Total { get; set; }
    public int NextOffset { get; set; } = -1;
    public List<PlayerRecord> Players { get; set; } = [];
}

public sealed class InventorySnapshot
{
    public string SteamId { get; set; } = "";
    public string WipeId { get; set; } = "";
    public string CapturedAt { get; set; } = "";
    public bool Current { get; set; }
    public string Source { get; set; } = "";
    public List<ItemRecord> Items { get; set; } = [];
}

public sealed class ItemRecord
{
    public int ItemId { get; set; }
    public string Container { get; set; } = "";
    public int Slot { get; set; }
    public string ShortName { get; set; } = "";
    public string Name { get; set; } = "";
    public int Amount { get; set; }
    public float Condition { get; set; }
    public float MaxCondition { get; set; }
    public int? Ammo { get; set; }
    public string Skin { get; set; } = "0";
    public List<ItemRecord> Contents { get; set; } = [];
}

public sealed class MapChunk
{
    public string WipeId { get; set; } = "";
    public int Offset { get; set; }
    public int Total { get; set; }
    public int OceanMargin { get; set; } = 500;
    public string Data { get; set; } = "";
}

public sealed record SshProfile(string Target, string Container)
{
    public string Key => $"ssh://{Target}/{Container}";
}

public sealed class SshServerSnapshot
{
    public ServerState Server { get; set; } = new();
    public List<PlayerRecord> Players { get; set; } = [];
    public List<InventorySnapshot> Inventories { get; set; } = [];
    public bool PresenceAvailable { get; set; }
    public bool HistoryAvailable { get; set; }
    public string Warning { get; set; } = "";
}

public sealed class SshMap
{
    public string WipeId { get; set; } = "";
    public string Data { get; set; } = "";
}

public sealed record ConnectionProfile(string Host, int Port, bool Tls)
{
    public string Key => $"{(Tls ? "wss" : "ws")}://{Host.ToLowerInvariant()}:{Port}";
    public override string ToString() => Key;
    public Uri Uri(string password)
    {
        if (string.IsNullOrWhiteSpace(Host) || System.Uri.CheckHostName(Host) == UriHostNameType.Unknown || Port is < 1 or > 65535)
            throw new ArgumentException("ホスト名とポートを確認してください。ホストには URL ではなくホスト名または IP を入力します。");
        return new UriBuilder(Tls ? "wss" : "ws", Host, Port) { Path = System.Uri.EscapeDataString(password) }.Uri;
    }
}
