using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;

namespace Oxide.Plugins
{
    [Info("RustMonitorAdmin", "Rust Monitor", "0.1.1")]
    [Description("Explicit server-console-only single-item deletion and single-account bans.")]
    public class RustMonitorAdmin : RustPlugin
    {
        private const string Protocol = "rust-monitor-admin/1";
        private readonly Dictionary<string, object> results = new Dictionary<string, object>();
        private readonly Dictionary<string, Upload> uploads = new Dictionary<string, Upload>();
        private class Upload { public DateTime Created = DateTime.UtcNow; public string[] Parts; }

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
            arg.ReplyWith(JsonConvert.SerializeObject(new { Protocol, Version = "0.1.1" }));
        }

        [ConsoleCommand("rustmonitoradmin.result")]
        private void ReadResult(ConsoleSystem.Arg arg)
        {
            if (arg.Connection != null) return;
            var id = arg.GetString(0, "");
            object result;
            if (!Regex.IsMatch(id, "\\A[0-9a-f]{32}\\z")) return;
            Reply(arg, results.TryGetValue(id, out result) ? result : Result(new Request { RequestId = id }, "unknown", "操作結果の記録がありません。自動再送しません。"));
        }

        [ConsoleCommand("rustmonitoradmin.prepare")]
        private void Prepare(ConsoleSystem.Arg arg)
        {
            if (arg.Connection != null) return;
            var id = arg.GetString(0, "");
            int index, total;
            var chunk = arg.GetString(3, "");
            if (!Regex.IsMatch(id, "\\A[0-9a-f]{32}\\z") || !int.TryParse(arg.GetString(1, ""), out index) ||
                !int.TryParse(arg.GetString(2, ""), out total) || total < 1 || total > 80 || index < 0 || index >= total ||
                !Regex.IsMatch(chunk, "\\A[A-Za-z0-9+/=]{1,600}\\z")) return;
            foreach (var expired in uploads.Where(p => p.Value.Created < DateTime.UtcNow.AddMinutes(-2)).Select(p => p.Key).ToList()) uploads.Remove(expired);
            Upload upload;
            if (!uploads.TryGetValue(id, out upload))
            {
                if (uploads.Count >= 32 || results.ContainsKey(id)) return;
                upload = new Upload { Parts = new string[total] }; uploads[id] = upload;
            }
            if (upload.Parts.Length != total || (upload.Parts[index] != null && upload.Parts[index] != chunk)) return;
            upload.Parts[index] = chunk;
            Reply(arg, new { Protocol, RequestId = id, Prepared = index });
        }

        [ConsoleCommand("rustmonitoradmin.commit")]
        private void Commit(ConsoleSystem.Arg arg)
        {
            if (arg.Connection != null) return;
            var id = arg.GetString(0, "");
            if (!Regex.IsMatch(id, "\\A[0-9a-f]{32}\\z")) return;
            object previous;
            if (results.TryGetValue(id, out previous)) { Reply(arg, previous); return; }
            Upload upload;
            if (!uploads.TryGetValue(id, out upload) || upload.Created < DateTime.UtcNow.AddMinutes(-2) || upload.Parts.Any(p => p == null))
            { Reply(arg, Result(new Request { RequestId = id }, "rejected", "アイテム情報の準備が完了していません。削除していません。")); return; }
            uploads.Remove(id);
            ExecuteData(arg, string.Concat(upload.Parts), id, false);
        }

        [ConsoleCommand("rustmonitoradmin.check")]
        private void CheckDelete(ConsoleSystem.Arg arg)
        {
            if (arg.Connection != null) return;
            ExecuteData(arg, arg.GetString(0, ""), null, true);
        }

        [ConsoleCommand("rustmonitoradmin.inspect")]
        private void Inspect(ConsoleSystem.Arg arg)
        {
            if (arg.Connection != null) return;
            ulong id;
            if (!ulong.TryParse(arg.GetString(0, ""), out id) || id < 70000000000000000UL) return;
            var player = BasePlayer.FindByID(id) ?? BasePlayer.FindSleeping(id);
            if (player == null || player.inventory == null) { Reply(arg, new { Protocol, Available = false }); return; }
            var rows = new List<object>();
            foreach (var container in new[] { player.inventory.containerMain, player.inventory.containerBelt, player.inventory.containerWear })
            {
                if (container == null) continue;
                foreach (var item in container.itemList)
                    rows.Add(new { Uid = item.uid.Value.ToString(), ItemId = item.info.itemid, Slot = item.position, Amount = item.amount,
                        Container = container == player.inventory.containerMain ? "main" : container == player.inventory.containerBelt ? "belt" : "wear",
                        HasCondition = item.hasCondition, RawCondition = item.condition, RawMaxCondition = item.maxCondition });
            }
            Reply(arg, new { Protocol, Available = true, Items = rows });
        }

        [ConsoleCommand("rustmonitoradmin.execute")]
        private void Execute(ConsoleSystem.Arg arg)
        {
            // RCON / local server console only, including rejection of in-game admins.
            if (arg.Connection != null) return;
            ExecuteData(arg, arg.GetString(0, ""), null, false);
        }
        private void ExecuteData(ConsoleSystem.Arg arg, string encoded, string expectedId, bool checkOnly)
        {
            Request request = null;
            bool started = false;
            try
            {
                if (encoded.Length > 48000) throw new ArgumentException();
                request = JsonConvert.DeserializeObject<Request>(Encoding.UTF8.GetString(Convert.FromBase64String(encoded)),
                    new JsonSerializerSettings { MaxDepth = 24 });
                ulong steamId;
                if (request == null || !Regex.IsMatch(request.RequestId ?? "", "\\A[0-9a-f]{32}\\z") ||
                    !Regex.IsMatch(request.SteamId ?? "", "\\A[0-9]{17}\\z") || !ulong.TryParse(request.SteamId, out steamId) ||
                    steamId < 70000000000000000UL || (request.Action != "delete" && request.Action != "ban") ||
                    (expectedId != null && request.RequestId != expectedId))
                    throw new ArgumentException();
                if (checkOnly)
                {
                    Reply(arg, request.Action == "delete" ? Delete(request, steamId, ref started, true) : Result(request, "rejected", "削除の照合専用です。"));
                    return;
                }
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
                if (!checkOnly && request != null && request.RequestId != null && results.ContainsKey(request.RequestId)) results[request.RequestId] = result;
                Reply(arg, result);
            }
        }

        private object Delete(Request request, ulong steamId, ref bool started, bool checkOnly = false)
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
            if (checkOnly) return Result(request, "ready", "現在の所持品と一致しています。照合のみで削除していません。");
            started = true;
            item.Remove();
            if (!item.IsRemoved()) return Result(request, "rejected", "サーバーのプラグインが削除を許可しませんでした。");
            item.RemoveFromContainer();
            Audit("delete " + request.SteamId + " item " + request.Item.Uid + " request " + request.RequestId);
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
            Audit("ban " + request.SteamId + " request " + request.RequestId);
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
                item.amount != spec.Amount || item.skin.ToString() != spec.Skin || Math.Abs((item.hasCondition ? item.condition : 0f) - spec.Condition) > .01f ||
                Math.Abs((item.hasCondition ? item.maxCondition : 0f) - spec.MaxCondition) > .01f) return false;
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
        private void Audit(string message)
        {
            // Puts during RCON execution is mistaken for the result by single-response clients.
            try { LogToFile("actions", message, this); } catch (Exception) { }
        }
    }
}
