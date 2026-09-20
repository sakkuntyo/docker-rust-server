using System.Text.RegularExpressions;

namespace RustMonitor.Core;

public sealed class ItemParent
{
    public string Uid { get; set; } = "";
    public int ItemId { get; set; }
    public int Slot { get; set; }
    public string Name { get; set; } = "";
}

public sealed class ModerationRequest
{
    public string RequestId { get; set; } = Guid.NewGuid().ToString("N");
    public string Action { get; set; } = "";
    public string SteamId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Reason { get; set; } = "";
    public string WipeId { get; set; } = "";
    public ItemRecord? Item { get; set; }
    public List<ItemParent> Parents { get; set; } = [];
    public static ModerationRequest DeleteItem(InventorySnapshot snapshot, ItemRecord selected, string name)
    {
        if (snapshot.Source is not ("save" or "live")) throw new ArgumentException("所持品を更新してください。");
        var matches = new List<(ItemRecord Item, string Container, List<ItemParent> Parents)>();
        void Find(IEnumerable<ItemRecord> items, string container, List<ItemParent> parents)
        {
            if (parents.Count > 6) return;
            foreach (var item in items)
            {
                if (ulong.TryParse(selected.Uid, out var uid) && uid > 0 && item.Uid == selected.Uid)
                    matches.Add((item, container, parents));
                ItemCatalog.Name(item);
                Find(item.Contents, container, [.. parents, new ItemParent { Uid = item.Uid, ItemId = item.ItemId, Slot = item.Slot, Name = item.Name }]);
            }
        }
        foreach (var root in snapshot.Items) Find([root], root.Container, []);
        if (matches.Count != 1) throw new ArgumentException("対象を特定できません。所持品を更新してください。");
        var found = matches[0];
        var action = new ModerationRequest { Action = "delete", SteamId = snapshot.SteamId, Name = name, WipeId = snapshot.WipeId,
            Item = Wire.Read<ItemRecord>(Wire.Write(found.Item)), Parents = found.Parents };
        action.Item.Container = found.Container;
        action.Validate();
        return action;
    }
    public ModerationRequest WithFreshIdentity(InventorySnapshot snapshot)
    {
        if (Action != "delete" || Item == null || snapshot.Source is not ("save" or "live") || snapshot.SteamId != SteamId || snapshot.WipeId != WipeId)
            throw new ArgumentException("プレイヤーまたはワイプが変わりました。所持品を選び直してください。");
        if (Parents.Count > 0) throw new ArgumentException("バッグの中身を更新してから選び直してください。");
        var matches = snapshot.Items.Where(i => i.Container == Item.Container && i.Slot == Item.Slot).ToArray();
        if (matches.Length != 1 || !SameSavedItem(Item, matches[0]))
            throw new ArgumentException("所持品の内容が変わっています。更新された一覧からアイテムを選び直してください。");
        var fresh = Wire.Read<ModerationRequest>(Wire.Write(this));
        fresh.Item = Wire.Read<ItemRecord>(Wire.Write(matches[0]));
        ItemCatalog.Name(fresh.Item);
        fresh.Validate();
        return fresh;
    }
    private static bool SameSavedItem(ItemRecord a, ItemRecord b) => a.ItemId == b.ItemId && a.Slot == b.Slot && a.Amount == b.Amount &&
        a.Skin == b.Skin && a.Condition == b.Condition && a.MaxCondition == b.MaxCondition && (a.Ammo ?? 0) == (b.Ammo ?? 0) &&
        (!ulong.TryParse(a.Uid, out var uid) || uid == 0 || a.Uid == b.Uid) && a.Contents.Count == b.Contents.Count &&
        a.Contents.OrderBy(i => i.Slot).Zip(b.Contents.OrderBy(i => i.Slot)).All(pair => SameSavedItem(pair.First, pair.Second));
    public void Validate()
    {
        if (!Regex.IsMatch(RequestId, @"\A[0-9a-f]{32}\z") || !Regex.IsMatch(SteamId, @"\A[0-9]{17}\z") ||
            !ulong.TryParse(SteamId, out var id) || id < 70000000000000000UL || Action is not ("delete" or "ban"))
            throw new ArgumentException("対象プレイヤーと操作内容を確認してください。");
        if (Action == "ban" && (string.IsNullOrWhiteSpace(Reason) || Reason.Length > 200 || Reason.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(Name) || Name.Length > 256 || Name.Any(char.IsControl)))
            throw new ArgumentException("BAN理由を1～200文字で入力してください。");
        if (Action == "delete" && (!Regex.IsMatch(WipeId, @"\Asave:[0-9]+:[A-Za-z0-9-]+\z") ||
            Item?.Container is not ("main" or "belt" or "wear") || !ValidItem(Item, 0) || Parents == null || Parents.Count > 6 ||
            Parents.Any(p => p == null || !ulong.TryParse(p.Uid, out var parentUid) || parentUid == 0 || p.ItemId == 0 || p.Slot is < 0 or > 1024) ||
            Parents.Select(p => p.Uid).Append(Item.Uid).Distinct().Count() != Parents.Count + 1))
            throw new ArgumentException("アイテムの識別情報がありません。SSHで所持品を更新してください。");
    }
    private static bool ValidItem(ItemRecord? item, int depth) => item != null && depth <= 6 &&
        ulong.TryParse(item.Uid, out var uid) && uid > 0 && item.ItemId != 0 && item.Slot is >= 0 and <= 1024 && item.Amount > 0 &&
        ulong.TryParse(item.Skin, out _) && float.IsFinite(item.Condition) && float.IsFinite(item.MaxCondition) &&
        item.Contents.Count <= 64 && item.Contents.All(child => ValidItem(child, depth + 1));
}

public sealed class ModerationResult
{
    public string State { get; set; } = "unknown";
    public string Message { get; set; } = "";
}
