using System.Reflection;
using System.Text;
using System.Text.Json;
using Newtonsoft.Json;
using Oxide.Plugins;

internal static class Program
{
    private const ulong PlayerId = 76561198000000001;
    private static int checks;
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; Console.WriteLine("PASS " + message); }
    private static RustMonitorAdmin.Request Request(string action = "delete") => new()
    {
        Action = action, RequestId = Guid.NewGuid().ToString("N"), SteamId = PlayerId.ToString(), Name = "Demo", Reason = "Example reason", WipeId = "save:1789171200:demo",
        Item = new() { Uid = "42", ItemId = 123, Slot = 0, Container = "belt", Amount = 2, Skin = "0", Condition = 50, MaxCondition = 100, Ammo = 31 }
    };
    private static (RustMonitorAdmin, BasePlayer, Item) Setup(bool sleeping = false)
    {
        BasePlayer.Active.Clear(); BasePlayer.Sleeping.Clear(); ServerUsers.Banned.Clear(); ServerUsers.FailSave = false; ServerUsers.Saves = 0;
        var player = new BasePlayer { IsConnected = !sleeping };
        (sleeping ? BasePlayer.Sleeping : BasePlayer.Active)[PlayerId] = player;
        var item = new Item { uid = new() { Value = 42 }, info = new() { itemid = 123 }, amount = 2, position = 0,
            condition = 50, maxCondition = 100, parent = player.inventory.containerBelt, Held = new BaseProjectile { primaryMagazine = new() { contents = 30 } } };
        item.parent.itemList.Add(item);
        return (new(), player, item);
    }
    private static string Run(RustMonitorAdmin plugin, RustMonitorAdmin.Request request, bool inGame = false)
    {
        var arg = new ConsoleSystem.Arg { Connection = inGame ? new object() : null, Input = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(request))) };
        typeof(RustMonitorAdmin).GetMethod("Execute", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(plugin, [arg]);
        return arg.Output == null ? "none" : JsonDocument.Parse(arg.Output).RootElement.GetProperty("State").GetString();
    }
    private static int Main()
    {
        foreach (var sleeping in new[] { false, true })
        {
            var (plugin, player, item) = Setup(sleeping); var request = Request();
            Check(Run(plugin, request) == "accepted" && item.Removed && item.parent == null && player.inventory.containerBelt.itemList.Count == 0,
                "matching item is removed from " + (sleeping ? "sleeping" : "online") + " player");
            Run(plugin, request); Check(item.RemoveCalls == 1, "duplicate request does not execute removal twice");
        }
        var changes = new Action<RustMonitorAdmin.Request, BasePlayer, Item>[] {
            (r,p,i) => r.WipeId = "save:1789171200:other", (r,p,i) => r.WipeId = "save:1789171201:demo",
            (r,p,i) => i.uid.Value = 99, (r,p,i) => i.info.itemid = 321, (r,p,i) => i.position = 1,
            (r,p,i) => i.amount = 3, (r,p,i) => i.skin = 99, (r,p,i) => i.condition = 49,
            (r,p,i) => ((BaseProjectile)i.Held).primaryMagazine.contents = 29,
            (r,p,i) => { p.inventory.containerBelt.itemList.Clear(); p.inventory.containerMain.itemList.Add(i); i.parent = p.inventory.containerMain; },
            (r,p,i) => r.SteamId = "76561198000000002", (r,p,i) => p.Dead = true,
            (r,p,i) => i.contents = new() { itemList = [new Item { uid = new() { Value = 88 } }] },
            (r,p,i) => r.Item.Uid = "0", (r,p,i) => r.Item.Container = "corpse"
        };
        foreach (var change in changes)
        {
            var (plugin, player, item) = Setup(); var request = Request(); change(request, player, item);
            Check(Run(plugin, request) == "rejected" && item.RemoveCalls == 0 && !item.Removed, "changed target / save / contents fails closed before removal");
        }
        foreach (var changed in new[] { false, true })
        {
            var (plugin, _, item) = Setup(); var request = Request();
            item.contents = new() { itemList = [new Item { uid = new() { Value = 43 }, info = new() { itemid = 321 }, amount = changed ? 2 : 1, position = 0 }] };
            request.Item.Contents.Add(new() { Uid = "43", ItemId = 321, Amount = 1, Slot = 0, Skin = "0" });
            Check(Run(plugin, request) == (changed ? "rejected" : "accepted") && item.Removed == !changed,
                "nested contents are compared before deleting their containing item");
        }
        {
            var (plugin, _, item) = Setup();
            Check(Run(plugin, Request(), true) == "none" && item.RemoveCalls == 0, "even in-game admins cannot execute the server-only operation");
            item.Denied = true;
            Check(Run(plugin, Request()) == "rejected" && item.parent != null, "a removal blocked by another plugin leaves the item attached");
        }
        {
            var (plugin, player, _) = Setup(); var request = Request("ban");
            var other = new BasePlayer { IsConnected = true }; BasePlayer.Active[PlayerId + 1] = other;
            Check(Run(plugin, request) == "accepted" && ServerUsers.Banned.SetEquals([PlayerId]) && ServerUsers.Saves == 1 && player.Kicked && !other.Kicked,
                "BAN persists only the selected account and disconnects only that player");
            Run(plugin, request); Check(ServerUsers.Saves == 1, "duplicate BAN request does not write again");
            Check(Run(plugin, Request("ban")) == "rejected", "already-banned account is reported without overwriting its reason");
        }
        {
            var (plugin, _, _) = Setup(true);
            Check(Run(plugin, Request("ban")) == "accepted" && ServerUsers.Banned.Contains(PlayerId), "offline accounts can be banned");
        }
        {
            var (plugin, _, _) = Setup(); ServerUsers.FailSave = true;
            Check(Run(plugin, Request("ban")) == "unknown", "failure after mutation is reported as uncertain instead of not executed");
        }
        Console.WriteLine($"{checks} admin plugin checks passed."); return 0;
    }
}
