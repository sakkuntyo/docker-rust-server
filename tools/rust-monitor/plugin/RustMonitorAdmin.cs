using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;

namespace Oxide.Plugins
{
    [Info("RustMonitorAdmin", "Rust Monitor", "0.1.0")]
    [Description("Explicit server-console-only single-item deletion and single-account bans.")]
    public class RustMonitorAdmin : RustPlugin
    {
        private const string Protocol = "rust-monitor-admin/1";
        private readonly Dictionary<string, object> results = new Dictionary<string, object>();

        public class ItemSpec
        {
            public string Uid, Container, Skin;
            public int ItemId, Slot, Amount;
            public float Condition, MaxCondition;
            public int? Ammo;
            public List<ItemSpec> Contents = new List<ItemSpec>();
        }
        public class Request
        {
            public string RequestId, Action, SteamId, Name, Reason, WipeId;
            public ItemSpec Item;
        }

        [ConsoleCommand("rustmonitoradmin.ping")]
        private void Ping(ConsoleSystem.Arg arg)
        {
            if (arg.Connection != null) return;
            arg.ReplyWith(JsonConvert.SerializeObject(new { Protocol }));
        }

        [ConsoleCommand("rustmonitoradmin.execute")]
        private void Execute(ConsoleSystem.Arg arg)
        {
            // RCON / local server console only, including rejection of in-game admins.
            if (arg.Connection != null) return;
            Request request = null;
            bool started = false;
            try
            {
                var encoded = arg.GetString(0, "");
                if (encoded.Length > 48000) throw new ArgumentException();
                request = JsonConvert.DeserializeObject<Request>(Encoding.UTF8.GetString(Convert.FromBase64String(encoded)),
                    new JsonSerializerSettings { MaxDepth = 24 });
                ulong steamId;
                if (request == null || !Regex.IsMatch(request.RequestId ?? "", "\\A[0-9a-f]{32}\\z") ||
                    !Regex.IsMatch(request.SteamId ?? "", "\\A[0-9]{17}\\z") || !ulong.TryParse(request.SteamId, out steamId) ||
                    steamId < 70000000000000000UL || (request.Action != "delete" && request.Action != "ban"))
                    throw new ArgumentException();
                object previous;
                if (results.TryGetValue(request.RequestId, out previous)) { Reply(arg, previous); return; }
                if (results.Count >= 4096) { Reply(arg, Result(request, "rejected", "管理操作の上限に達しました。管理プラグインを再ロードしてください。")); return; }
                // Record the attempt before any mutation. Never repeat an uncertain action.
                results[request.RequestId] = Result(request, "unknown", "実行結果は未確認です。再送していません。");
                object result;
                if (request.Action == "delete") result = Delete(request, steamId, ref started);
                else result = Ban(request, steamId, ref started);
                results[request.RequestId] = result;
                Reply(arg, result);
            }
            catch (Exception)
            {
                var result = Result(request, started ? "unknown" : "rejected",
                    started ? "操作結果を確認できません。ゲーム内またはBAN一覧を確認してください。" : "操作内容を確認できません。変更していません。");
                if (request != null && request.RequestId != null && results.ContainsKey(request.RequestId)) results[request.RequestId] = result;
                Reply(arg, result);
            }
        }

        private object Delete(Request request, ulong steamId, ref bool started)
        {
            var wipe = "save:" + new DateTimeOffset(SaveRestore.SaveCreatedTime.ToUniversalTime()).ToUnixTimeSeconds() + ":" + SaveRestore.WipeId;
            if (request.WipeId != wipe || !ValidItem(request.Item, 0) ||
                (request.Item.Container != "main" && request.Item.Container != "belt" && request.Item.Container != "wear"))
                return Result(request, "rejected", "ワイプまたはアイテムの識別情報が一致しません。所持品を更新してください。");
            var player = BasePlayer.FindByID(steamId) ?? BasePlayer.FindSleeping(steamId);
            if (player == null || player.IsDead() || player.inventory == null)
                return Result(request, "rejected", "本人の身体が見つかりません。削除していません。");
            var container = request.Item.Container == "main" ? player.inventory.containerMain :
                request.Item.Container == "belt" ? player.inventory.containerBelt : player.inventory.containerWear;
            var item = container == null ? null : container.itemList.FirstOrDefault(i => i.uid.Value.ToString() == request.Item.Uid);
            // Compare live identity, owner, position, stack, durability, ammunition and children
            // in the same server tick as removal. Never delete a replacement in the old slot.
            if (item == null || item.parent != container || !Matches(item, request.Item))
                return Result(request, "rejected", "セーブ後に対象が移動・変更されています。削除していません。次のセーブ後に更新してください。");
            started = true;
            item.Remove();
            if (!item.IsRemoved()) return Result(request, "rejected", "サーバーのプラグインが削除を許可しませんでした。");
            item.RemoveFromContainer();
            Puts("delete " + request.SteamId + " item " + request.Item.Uid + " request " + request.RequestId);
            return Result(request, "accepted", "対象のアイテム（スタック全体・内容物を含む）を削除しました。所持品の表示は次のセーブ後に更新されます。");
        }

        private object Ban(Request request, ulong steamId, ref bool started)
        {
            if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > 200 || request.Reason.Any(char.IsControl) ||
                string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 256 || request.Name.Any(char.IsControl))
                return Result(request, "rejected", "プレイヤー名とBAN理由（1～200文字）を確認してください。");
            if (ServerUsers.Is(steamId, ServerUsers.UserGroup.Banned))
                return Result(request, "rejected", "このSteam IDはすでにBANされています。");
            started = true;
            // Ban exactly the selected account, not a family-sharing owner or matching name.
            ServerUsers.Set(steamId, ServerUsers.UserGroup.Banned, request.Name, request.Reason, -1);
            ServerUsers.Save();
            var player = BasePlayer.FindByID(steamId);
            if (player != null && player.IsConnected) player.Kick(request.Reason);
            Puts("ban " + request.SteamId + " request " + request.RequestId);
            return Result(request, "accepted", "このサーバーで対象のSteam IDを無期限BANしました。接続中の場合は切断します。");
        }

        private static bool ValidItem(ItemSpec item, int depth)
        {
            ulong uid, skin;
            return item != null && depth <= 6 && ulong.TryParse(item.Uid, out uid) && uid > 0 &&
                ulong.TryParse(item.Skin, out skin) && item.ItemId != 0 && item.Slot >= 0 && item.Slot <= 1024 && item.Amount > 0 &&
                !float.IsNaN(item.Condition) && !float.IsInfinity(item.Condition) && !float.IsNaN(item.MaxCondition) && !float.IsInfinity(item.MaxCondition) &&
                item.Contents != null && item.Contents.Count <= 64 && item.Contents.All(child => ValidItem(child, depth + 1)) &&
                item.Contents.Select(child => child.Uid).Distinct().Count() == item.Contents.Count;
        }
        private static bool Matches(Item item, ItemSpec spec)
        {
            if (item.IsRemoved() || item.uid.Value.ToString() != spec.Uid || item.info.itemid != spec.ItemId || item.position != spec.Slot ||
                item.amount != spec.Amount || item.skin.ToString() != spec.Skin || Math.Abs(item.condition - spec.Condition) > .01f ||
                Math.Abs(item.maxCondition - spec.MaxCondition) > .01f) return false;
            // Rust saves ammoCount as live count + 1, zero for non-ammunition items.
            var entity = item.GetHeldEntity();
            var ammo = entity is BaseProjectile ? ((BaseProjectile)entity).primaryMagazine.contents + 1 :
                entity is Chainsaw ? ((Chainsaw)entity).ammo + 1 : entity is FlameThrower ? ((FlameThrower)entity).ammo + 1 : 0;
            if (ammo != (spec.Ammo ?? 0)) return false;
            var contents = item.contents == null ? new List<Item>() : item.contents.itemList;
            return contents.Count == spec.Contents.Count && spec.Contents.All(expected =>
                contents.Any(child => child.uid.Value.ToString() == expected.Uid && Matches(child, expected)));
        }
        private static object Result(Request request, string state, string message)
        { return new { Protocol, RequestId = request == null ? "" : request.RequestId, State = state, Message = message }; }
        private static void Reply(ConsoleSystem.Arg arg, object result) { arg.ReplyWith(JsonConvert.SerializeObject(result)); }
    }
}
