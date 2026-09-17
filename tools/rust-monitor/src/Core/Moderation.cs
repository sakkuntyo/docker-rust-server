using System.Text.RegularExpressions;

namespace RustMonitor.Core;

public sealed class ModerationRequest
{
    public string RequestId { get; set; } = Guid.NewGuid().ToString("N");
    public string Action { get; set; } = "";
    public string SteamId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Reason { get; set; } = "";
    public string WipeId { get; set; } = "";
    public ItemRecord? Item { get; set; }
    public void Validate()
    {
        if (!Regex.IsMatch(RequestId, @"\A[0-9a-f]{32}\z") || !Regex.IsMatch(SteamId, @"\A[0-9]{17}\z") ||
            !ulong.TryParse(SteamId, out var id) || id < 70000000000000000UL || Action is not ("delete" or "ban"))
            throw new ArgumentException("対象プレイヤーと操作内容を確認してください。");
        if (Action == "ban" && (string.IsNullOrWhiteSpace(Reason) || Reason.Length > 200 || Reason.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(Name) || Name.Length > 256 || Name.Any(char.IsControl)))
            throw new ArgumentException("BAN理由を1～200文字で入力してください。");
        if (Action == "delete" && (!Regex.IsMatch(WipeId, @"\Asave:[0-9]+:[A-Za-z0-9-]+\z") ||
            Item?.Container is not ("main" or "belt" or "wear") || !ValidItem(Item, 0)))
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
