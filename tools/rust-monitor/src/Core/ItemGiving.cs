using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace RustMonitor.Core;

public sealed class CatalogItem
{
    public int ItemId { get; set; }
    public string Name { get; set; } = "";
    public string ShortName { get; set; } = "";
    public string Category { get; set; } = "Misc";
    public int StackSize { get; set; } = 1;
    [JsonIgnore] public string DisplayName => JapaneseNames.GetValueOrDefault(ShortName, Name);
    private static readonly Dictionary<string, string> JapaneseNames = new()
    {
        ["rifle.ak"] = "アサルトライフル", ["rifle.lr300"] = "LR-300アサルトライフル", ["smg.mp5"] = "MP5A4", ["smg.2"] = "カスタムSMG",
        ["smg.thompson"] = "トンプソン", ["pistol.revolver"] = "リボルバー", ["pistol.semiauto"] = "セミオートピストル", ["pistol.m92"] = "M92ピストル",
        ["rifle.bolt"] = "ボルトアクションライフル", ["rifle.semiauto"] = "セミオートライフル", ["crossbow"] = "クロスボウ", ["bow.hunting"] = "ハンティングボウ",
        ["shotgun.pump"] = "ポンプショットガン", ["grenade.f1"] = "F1グレネード", ["rocket.launcher"] = "ロケットランチャー",
        ["ammo.rifle"] = "5.56ライフル弾", ["ammo.pistol"] = "ピストル弾", ["ammo.rocket.basic"] = "ロケット弾",
        ["wood"] = "木材", ["stones"] = "石", ["metal.fragments"] = "金属片", ["metal.refined"] = "上質金属", ["cloth"] = "布", ["scrap"] = "スクラップ",
        ["sulfur"] = "硫黄", ["charcoal"] = "木炭", ["gunpowder"] = "火薬", ["lowgradefuel"] = "低質燃料", ["leather"] = "革",
        ["syringe.medical"] = "医療用注射器", ["bandage"] = "包帯", ["largemedkit"] = "大型医療キット", ["hazmatsuit"] = "防護服",
        ["hammer"] = "ハンマー", ["hatchet"] = "斧", ["pickaxe"] = "ピッケル", ["jackhammer"] = "ジャックハンマー"
    };
    public bool Matches(string query) => string.IsNullOrWhiteSpace(query) || new[] { Name, DisplayName, ShortName, ItemId.ToString() }
        .Any(value => value.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase));
}

public sealed class ItemMenuSnapshot
{
    public string SteamId { get; set; } = "";
    public bool Online { get; set; }
    public List<CatalogItem> Items { get; set; } = [];
}

public sealed record GiveItemRequest(string SteamId, int ItemId, string ShortName, int Amount)
{
    public const int MaximumAmount = 10000;
    public void Validate()
    {
        if (!Regex.IsMatch(SteamId, @"\A[0-9]{17}\z") || !ValidShortName(ShortName) || ItemId == 0 || Amount is < 1 or > MaximumAmount)
            throw new ArgumentException("プレイヤー・アイテム・数量（1～10,000）を確認してください。");
    }
    public static bool ValidShortName(string name) => Regex.IsMatch(name, @"\A[a-z0-9][a-z0-9._ -]{0,95}\z") && name == name.Trim();
}

public sealed class GiveItemResult
{
    public string State { get; set; } = "unknown";
    public string Message { get; set; } = "";
}
