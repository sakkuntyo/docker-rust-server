using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Oxide.Core;

namespace Oxide.Plugins
{
    [Info("RustMonitorCollector", "nomarust", "0.1.0")]
    [Description("Read-only player history, inventory snapshots and map export for Rust Monitor.")]
    public class RustMonitorCollector : RustPlugin
    {
        private Persisted data;
        private string wipe;
        private byte[] map;
        private Queue<BasePlayer> scan = new Queue<BasePlayer>();
        private bool dirty;
        private bool ready;
        private static string Now() { return DateTime.UtcNow.ToString("O"); }

        private class Persisted
        {
            public string CollectorStartedAt = Now();
            public Dictionary<string, PlayerInfo> Players = new Dictionary<string, PlayerInfo>();
            public Dictionary<string, InventoryInfo> Inventories = new Dictionary<string, InventoryInfo>();
        }
        private class PlayerInfo
        {
            public string SteamId, Name, FirstSeen, LastSeen, LastConnected, LastDisconnected, ObservedAt, WipeId;
            public bool Online, BodyAvailable;
            public double? X, Z;
        }
        private class InventoryInfo
        {
            public string SteamId, WipeId, CapturedAt;
            public bool Current;
            public List<ItemInfo> Items = new List<ItemInfo>();
        }
        private class ItemInfo
        {
            public string Container, ShortName, Name, Skin;
            public int Slot, Amount;
            public float Condition, MaxCondition;
            public int? Ammo;
            public List<ItemInfo> Contents = new List<ItemInfo>();
        }
        private void Init()
        {
            try { data = Interface.Oxide.DataFileSystem.ReadObject<Persisted>(Name); }
            catch (Exception ex) { PrintError("Cannot read collector data; refusing to replace it: " + ex.Message); return; }
            if (data == null) data = new Persisted();
            foreach (var p in data.Players.Values) { p.Online = false; p.BodyAvailable = false; }
        }
        private void OnServerInitialized()
        {
            if (data == null) return;
            wipe = SaveRestore.SaveCreatedTime.ToUniversalTime().ToString("O") + ":" + World.Seed + ":" + World.Size;
            foreach (var p in data.Players.Values)
            {
                p.Online = false; p.BodyAvailable = false; p.ObservedAt = Now();
                if (p.WipeId != wipe) { p.X = null; p.Z = null; }
            }
            foreach (var key in data.Inventories.Where(kv => kv.Value.WipeId != wipe).Select(kv => kv.Key).ToList()) data.Inventories.Remove(key);
            ready = true;
            // Bootstrap identity immediately; inventory work is spread across ticks.
            foreach (var p in BasePlayer.activePlayerList) Observe(p);
            foreach (var p in BasePlayer.sleepingPlayerList) Observe(p);
            timer.Every(1f, Scan);
            timer.Every(10f, RefreshPresence);
            timer.Every(30f, Flush);
            dirty = true;
        }
        private bool Human(BasePlayer p) { return p != null && p.userID >= 76561197960265728UL && p.userID <= 76561202255233023UL; }
        private PlayerInfo Observe(BasePlayer p)
        {
            if (!Human(p)) return null;
            PlayerInfo info;
            var id = p.UserIDString;
            if (!data.Players.TryGetValue(id, out info))
            { info = new PlayerInfo { SteamId = id, FirstSeen = Now() }; data.Players[id] = info; }
            info.Name = p.displayName; info.Online = p.IsConnected; info.BodyAvailable = !p.IsDead(); info.ObservedAt = Now(); info.WipeId = wipe;
            if (p.IsConnected) info.LastSeen = info.ObservedAt;
            if (!p.IsDead()) { info.X = p.transform.position.x; info.Z = p.transform.position.z; }
            else { info.X = null; info.Z = null; }
            dirty = true;
            return info;
        }
        private InventoryInfo Capture(BasePlayer p)
        {
            var info = Observe(p);
            if (info == null || !info.BodyAvailable || p.inventory == null) return null;
            var snapshot = new InventoryInfo { SteamId = p.UserIDString, WipeId = wipe, CapturedAt = Now(), Current = true };
            AddContainer(snapshot, p.inventory.containerMain, "main"); AddContainer(snapshot, p.inventory.containerBelt, "belt"); AddContainer(snapshot, p.inventory.containerWear, "wear");
            data.Inventories[p.UserIDString] = snapshot; dirty = true; return snapshot;
        }
        private void AddContainer(InventoryInfo snapshot, ItemContainer container, string kind)
        { if (container != null) foreach (var item in container.itemList) snapshot.Items.Add(ReadItem(item, kind, 0)); }
        private ItemInfo ReadItem(Item item, string kind, int depth)
        {
            var info = new ItemInfo { Container = kind, Slot = item.position, ShortName = item.info.shortname, Name = item.info.displayName.english, Amount = item.amount, Condition = item.condition, MaxCondition = item.maxCondition, Skin = item.skin.ToString() };
            var weapon = item.GetHeldEntity() as BaseProjectile;
            if (weapon != null && weapon.primaryMagazine != null) info.Ammo = weapon.primaryMagazine.contents;
            if (depth < 6 && item.contents != null) foreach (var child in item.contents.itemList) info.Contents.Add(ReadItem(child, "contents", depth + 1));
            return info;
        }
        private void Scan()
        {
            if (!ready) return;
            if (scan.Count == 0)
            {
                foreach (var p in BasePlayer.activePlayerList) if (Human(p)) scan.Enqueue(p);
                foreach (var p in BasePlayer.sleepingPlayerList) if (Human(p)) scan.Enqueue(p);
            }
            for (var i = 0; i < 4 && scan.Count > 0; i++)
            {
                var player = scan.Dequeue();
                if (Human(player)) Capture(player);
            }
        }
        private void RefreshPresence()
        {
            if (!ready) return;
            foreach (var p in data.Players.Values) { p.Online = false; p.BodyAvailable = false; p.ObservedAt = Now(); }
            foreach (var p in BasePlayer.activePlayerList) Observe(p);
            foreach (var p in BasePlayer.sleepingPlayerList) Observe(p);
            dirty = true;
        }
        private void OnPlayerConnected(BasePlayer player)
        { if (!ready) return; var info = Observe(player); if (info != null) info.LastConnected = Now(); }
        private void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            if (!ready) return;
            Capture(player); PlayerInfo info;
            if (data.Players.TryGetValue(player.UserIDString, out info)) { info.Online = false; info.LastDisconnected = Now(); info.LastSeen = Now(); dirty = true; }
            Flush();
        }
        private void OnPlayerDeath(BasePlayer player, HitInfo hit)
        {
            if (!ready || !Human(player)) return;
            var id = player.UserIDString;
            NextTick(() => { PlayerInfo p; if (data.Players.TryGetValue(id, out p)) { p.BodyAvailable = false; p.X = null; p.Z = null; dirty = true; } });
        }
        private void OnServerSave() { Flush(); }
        private void Unload() { Flush(); }
        private void Flush()
        {
            if (data == null || !dirty) return;
            try { Interface.Oxide.DataFileSystem.WriteObject(Name, data); dirty = false; }
            catch (Exception ex) { PrintError("Collector save failed: " + ex.Message); }
        }
        private bool Allowed(ConsoleSystem.Arg arg)
        {
            // Authenticated RCON/server console only; even in-game admins cannot use this export.
            if (arg.Connection != null) { Reply(arg, new { Error = "Server console / RCON only" }); return false; }
            if (!ready) { Reply(arg, new { Error = "Collector is not ready; check server plugin logs" }); return false; }
            return true;
        }
        private void Reply(ConsoleSystem.Arg arg, object value) { arg.ReplyWith(JsonConvert.SerializeObject(value)); }
        [ConsoleCommand("rustmonitor.server")]
        private void ServerCommand(ConsoleSystem.Arg arg)
        {
            if (!Allowed(arg)) return;
            Reply(arg, new { Protocol = 1, Name = ConVar.Server.hostname, WipeId = wipe, Seed = World.Seed, Size = World.Size, CapturedAt = Now(), data.CollectorStartedAt });
        }
        [ConsoleCommand("rustmonitor.players")]
        private void PlayersCommand(ConsoleSystem.Arg arg)
        {
            if (!Allowed(arg)) return;
            var offset = arg.GetInt(0, 0);
            if (offset < 0) { Reply(arg, new { Error = "Invalid offset" }); return; }
            var all = data.Players.Values.OrderBy(p => p.SteamId, StringComparer.Ordinal).ToList();
            var page = all.Skip(offset).Take(100).ToList();
            Reply(arg, new { WipeId = wipe, Total = all.Count, NextOffset = offset + page.Count < all.Count ? offset + page.Count : -1, Players = page });
        }
        [ConsoleCommand("rustmonitor.inventory")]
        private void InventoryCommand(ConsoleSystem.Arg arg)
        {
            if (!Allowed(arg)) return;
            var id = arg.GetString(0, ""); ulong number;
            if (!ulong.TryParse(id, out number)) { Reply(arg, new { Error = "Invalid Steam ID" }); return; }
            var player = BasePlayer.FindByID(number) ?? BasePlayer.FindSleeping(number);
            var snapshot = Human(player) ? Capture(player) : null;
            if (snapshot == null)
            {
                if (!data.Inventories.TryGetValue(id, out snapshot) || snapshot.WipeId != wipe)
                    snapshot = new InventoryInfo { SteamId = id, WipeId = wipe };
                snapshot.Current = false;
            }
            Reply(arg, snapshot);
        }
        [ConsoleCommand("rustmonitor.map")]
        private void MapCommand(ConsoleSystem.Arg arg)
        {
            if (!Allowed(arg)) return;
            // Reuse the game's existing cache; never trigger expensive terrain rendering here.
            if (map == null) map = CompanionServer.Handlers.Map.ImageData;
            if (map == null || map.Length == 0) { Reply(arg, new { Error = "Rust+ map cache is not available yet" }); return; }
            var offset = arg.GetInt(0, 0);
            if (offset < 0 || offset >= map.Length) { Reply(arg, new { Error = "Invalid offset" }); return; }
            var length = Math.Min(32768, map.Length - offset);
            Reply(arg, new { WipeId = wipe, Offset = offset, Total = map.Length, OceanMargin = 500, Data = Convert.ToBase64String(map, offset, length) });
        }
    }
}
