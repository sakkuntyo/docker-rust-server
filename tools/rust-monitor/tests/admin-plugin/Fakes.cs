// Deterministic test doubles for admin logic. Compile against actual server DLLs separately.
using System.Text.Json;
namespace Newtonsoft.Json
{
    public class JsonSerializerSettings { public int MaxDepth { get; set; } }
    public static class JsonConvert
    {
        public static string SerializeObject(object value) => JsonSerializer.Serialize(value, new JsonSerializerOptions { IncludeFields = true });
        public static T DeserializeObject<T>(string value, JsonSerializerSettings settings) => JsonSerializer.Deserialize<T>(value, new JsonSerializerOptions { IncludeFields = true, MaxDepth = settings.MaxDepth });
    }
}
namespace Oxide.Plugins
{
    public class RustPlugin { protected void Puts(string text) { } }
    public class InfoAttribute : Attribute { public InfoAttribute(string name, string author, string version) { } }
    public class DescriptionAttribute : Attribute { public DescriptionAttribute(string text) { } }
    public class ConsoleCommandAttribute : Attribute { public ConsoleCommandAttribute(string name) { } }
}
public static class ConsoleSystem
{
    public class Arg
    {
        public object Connection;
        public string Input, Output;
        public string GetString(int i, string fallback) => Input ?? fallback;
        public void ReplyWith(string value) => Output = value;
    }
}
public static class SaveRestore
{
    public static DateTime SaveCreatedTime = DateTimeOffset.FromUnixTimeSeconds(1789171200).UtcDateTime;
    public static string WipeId = "demo";
}
public class BasePlayer
{
    public static Dictionary<ulong, BasePlayer> Active = new(), Sleeping = new();
    public static BasePlayer FindByID(ulong id) => Active.GetValueOrDefault(id);
    public static BasePlayer FindSleeping(ulong id) => Sleeping.GetValueOrDefault(id);
    public PlayerInventory inventory = new();
    public bool IsConnected, Dead, Kicked;
    public bool IsDead() => Dead;
    public void Kick(string reason) { Kicked = true; IsConnected = false; }
}
public class PlayerInventory { public ItemContainer containerMain = new(), containerBelt = new(), containerWear = new(); }
public class ItemContainer { public List<Item> itemList = new(); }
public struct ItemId { public ulong Value; }
public class ItemDefinition { public int itemid; }
public class Item
{
    public ItemId uid;
    public ItemDefinition info = new();
    public int position, amount;
    public ulong skin;
    public float condition, maxCondition;
    public ItemContainer parent, contents;
    public object Held;
    public bool Removed, Denied;
    public int RemoveCalls;
    public object GetHeldEntity() => Held;
    public bool IsRemoved() => Removed;
    public void Remove() { RemoveCalls++; if (!Denied) Removed = true; }
    public void RemoveFromContainer() { parent?.itemList.Remove(this); parent = null; }
}
public class BaseProjectile { public Magazine primaryMagazine = new(); public class Magazine { public int contents; } }
public class Chainsaw { public int ammo; }
public class FlameThrower { public int ammo; }
public static class ServerUsers
{
    public enum UserGroup { Banned }
    public static HashSet<ulong> Banned = new();
    public static int Saves;
    public static bool FailSave;
    public static bool Is(ulong id, UserGroup group) => Banned.Contains(id);
    public static void Set(ulong id, UserGroup group, string name, string reason, long expiry) => Banned.Add(id);
    public static void Save() { if (FailSave) throw new IOException(); Saves++; }
}
