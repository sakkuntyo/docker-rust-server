using System.Runtime.InteropServices;
using System.IO;
using System.Text;

namespace RustMonitor.Core;

// Windows ships and services SQLite. All values go through bound parameters.
public sealed class Store : IDisposable
{
    private IntPtr db;
    public Store(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var code = Native.sqlite3_open_v2(Utf8(path), out db, 2 | 4 | 65536, IntPtr.Zero);
        if (code != 0) { var error = Error(); Dispose(); throw error; }
        Native.sqlite3_busy_timeout(db, 5000);
        Run("PRAGMA journal_mode=WAL");
        Run("CREATE TABLE IF NOT EXISTS state (key TEXT PRIMARY KEY, value TEXT NOT NULL)");
        Run("CREATE TABLE IF NOT EXISTS players (server TEXT NOT NULL, steamid TEXT NOT NULL, data TEXT NOT NULL, PRIMARY KEY(server,steamid))");
        Run("CREATE TABLE IF NOT EXISTS inventories (server TEXT NOT NULL, wipe TEXT NOT NULL, steamid TEXT NOT NULL, data TEXT NOT NULL, PRIMARY KEY(server,wipe,steamid))");
    }
    public void Put(string key, string value) => Run("INSERT OR REPLACE INTO state VALUES (?,?)", key, value);
    public string? Get(string key) => Query("SELECT value FROM state WHERE key=?", key).FirstOrDefault();
    public void SaveRoster(string server, ServerState state, IEnumerable<PlayerRecord> players)
    {
        Run("BEGIN IMMEDIATE");
        try
        {
            foreach (var player in players)
                Run("INSERT OR REPLACE INTO players VALUES (?,?,?)", server, player.SteamId, Wire.Write(player));
            Put("server:" + server, Wire.Write(state));
            Run("COMMIT");
        }
        catch { Run("ROLLBACK"); throw; }
    }
    public List<PlayerRecord> Players(string server) => Query("SELECT data FROM players WHERE server=?", server).Select(Wire.Read<PlayerRecord>).ToList();
    public void SaveSshSnapshot(SshProfile profile, SshServerSnapshot snapshot)
    {
        if (snapshot.Server.Protocol != 1 || string.IsNullOrEmpty(snapshot.Server.WipeId))
            throw new InvalidDataException("シーズンを確認できないため、保存済み情報を表示します。");
        var incoming = snapshot.Players.ToDictionary(p => p.SteamId);
        if (incoming.Values.Any(p => !System.Text.RegularExpressions.Regex.IsMatch(p.SteamId, @"\A\d{17}\z") || p.WipeId != snapshot.Server.WipeId))
            throw new InvalidDataException("メンバーのワイプまたは ID が一致しません。");
        if (snapshot.Inventories.Any(i => i.WipeId != snapshot.Server.WipeId || !incoming.ContainsKey(i.SteamId)))
            throw new InvalidDataException("所持品のワイプまたはプレイヤーが一致しません。");
        foreach (var old in Players(profile.Key))
        {
            if (incoming.TryGetValue(old.SteamId, out var current))
            {
                if (DateTimeOffset.TryParse(old.FirstSeen, out var previousFirst) && (!DateTimeOffset.TryParse(current.FirstSeen, out var first) || previousFirst < first)) current.FirstSeen = old.FirstSeen;
                if (current.LastSeen.Length == 0) current.LastSeen = old.LastSeen;
                if (!current.IpVerified) { current.RealIp = old.RealIp; current.IpCheckedAt = old.IpCheckedAt; }
            }
            else
            {
                old.Online = false; old.BodyAvailable = false; old.X = null; old.Y = null; old.Z = null;
                old.PositionAt = ""; old.PositionReason = "最新セーブに本人の座標がありません";
                old.IpVerified = false; old.IpReason = ""; old.Address = ""; old.ConnectionSeconds = null;
                old.ObservedAt = snapshot.PresenceAvailable ? snapshot.Server.CapturedAt : "";
                incoming[old.SteamId] = old;
            }
        }
        Run("BEGIN IMMEDIATE");
        try
        {
            foreach (var player in incoming.Values)
                Run("INSERT OR REPLACE INTO players VALUES (?,?,?)", profile.Key, player.SteamId, Wire.Write(player));
            Put("server:" + profile.Key, Wire.Write(snapshot.Server));
            foreach (var inventory in snapshot.Inventories) SaveInventory(profile.Key, inventory);
            Run("COMMIT");
        }
        catch { Run("ROLLBACK"); throw; }
    }
    public void SaveInventory(string server, InventorySnapshot inventory) => Run("INSERT OR REPLACE INTO inventories VALUES (?,?,?,?)", server, inventory.WipeId, inventory.SteamId, Wire.Write(inventory));
    public InventorySnapshot? Inventory(string server, string wipe, string steamid)
    {
        var json = Query("SELECT data FROM inventories WHERE server=? AND wipe=? AND steamid=?", server, wipe, steamid).FirstOrDefault();
        return json == null ? null : Wire.Read<InventorySnapshot>(json);
    }
    private static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value + "\0");
    private Exception Error() => new InvalidOperationException("SQLite: " + Marshal.PtrToStringUTF8(Native.sqlite3_errmsg(db)));
    private IntPtr Prepare(string sql, string[] args)
    {
        if (Native.sqlite3_prepare_v2(db, Utf8(sql), -1, out var stmt, IntPtr.Zero) != 0) throw Error();
        try
        {
            for (var i = 0; i < args.Length; i++)
            {
                var bytes = Encoding.UTF8.GetBytes(args[i]);
                // Non-null buffer for empty strings; TRANSIENT copies the buffer before return.
                if (Native.sqlite3_bind_text(stmt, i + 1, Utf8(args[i]), bytes.Length, new IntPtr(-1)) != 0) throw Error();
            }
            return stmt;
        }
        catch { Native.sqlite3_finalize(stmt); throw; }
    }
    private void Run(string sql, params string[] args) => Query(sql, args);
    private List<string> Query(string sql, params string[] args)
    {
        var stmt = Prepare(sql, args);
        try
        {
            var results = new List<string>();
            int result;
            while ((result = Native.sqlite3_step(stmt)) == 100)
                results.Add(Marshal.PtrToStringUTF8(Native.sqlite3_column_text(stmt, 0)) ?? "");
            if (result != 101) throw Error();
            return results;
        }
        finally { Native.sqlite3_finalize(stmt); }
    }
    public void Dispose() { if (db != IntPtr.Zero) { Native.sqlite3_close_v2(db); db = IntPtr.Zero; } }
    private static class Native
    {
        private const string Dll = "winsqlite3.dll";
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_open_v2(byte[] path, out IntPtr db, int flags, IntPtr vfs);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_close_v2(IntPtr db);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_busy_timeout(IntPtr db, int ms);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr sqlite3_errmsg(IntPtr db);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int n, out IntPtr stmt, IntPtr tail);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_bind_text(IntPtr stmt, int index, byte[] value, int n, IntPtr destructor);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_step(IntPtr stmt);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr sqlite3_column_text(IntPtr stmt, int col);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_finalize(IntPtr stmt);
    }
}
