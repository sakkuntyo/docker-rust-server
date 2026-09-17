using System.IO;
using System.Text.Json.Serialization;

namespace RustMonitor.Core;

public sealed class CombatRow
{
    public double AgeSeconds { get; set; }
    // attacker, attacker entity ID, target, target entity ID, weapon, ammo,
    // area, distance, old/new HP, info, hits, integrity, travel, mismatch, desync.
    public string[] Fields { get; set; } = [];
}

public sealed class CombatSnapshot
{
    public string SteamId { get; set; } = "";
    public string Epoch { get; set; } = "";
    public string CheckedAt { get; set; } = "";
    public double SampleSeconds { get; set; }
    public double UncertaintySeconds { get; set; }
    public bool Available { get; set; }
    public string Reason { get; set; } = "";
    public List<CombatRow> Rows { get; set; } = [];
}

public sealed class CombatEntry
{
    public DateTimeOffset OccurredAt { get; set; }
    public string[] Fields { get; set; } = [];
    [JsonIgnore] public string TimeText => OccurredAt.ToLocalTime().ToString("MM/dd HH:mm:ss.ff");
    [JsonIgnore] public string Direction => Fields[0] == "you" ? "与ダメージ" : Fields[2] == "you" ? "被ダメージ" : "その他";
    [JsonIgnore] public string Attacker => (Fields[0] == "you" ? "本人" : Fields[0]) + "\nID: " + Fields[1];
    [JsonIgnore] public string Target => (Fields[2] == "you" ? "本人" : Fields[2]) + "\nID: " + Fields[3];
    [JsonIgnore] public string Weapon => Fields[4].Split('/').Last().Replace(".prefab", "");
    [JsonIgnore] public string Area => Fields[6];
    [JsonIgnore] public string Distance => Fields[7];
    [JsonIgnore] public string Health => Fields[8] + " → " + Fields[9];
    [JsonIgnore] public string Info => Fields[10];
    [JsonIgnore] public string Details => "武器: " + Fields[4] + "\n弾薬: " + Fields[5] + "\nhits: " + Fields[11] + " / integrity: " + Fields[12] +
        "\ntravel: " + Fields[13] + " / mismatch: " + Fields[14] + " / desync: " + Fields[15] + "\nIDはサーバー内のエンティティIDです。";
}

public sealed class CombatHistory
{
    public const int Limit = 10000;
    public List<CombatEntry> Entries { get; set; } = [];
    public CombatSnapshot? Previous { get; set; }
    public static string Key(SshProfile profile, string steamId) => "combat:" + profile.Key + ":" + steamId;

    public (List<CombatEntry> Added, string Notice) Append(CombatSnapshot next, string steamId)
    {
        if (next.SteamId != steamId || string.IsNullOrEmpty(next.Epoch) ||
            !DateTimeOffset.TryParse(next.CheckedAt, out var captured) || !double.IsFinite(next.SampleSeconds) ||
            !double.IsFinite(next.UncertaintySeconds) || next.UncertaintySeconds is < 0 or > 30 ||
            next.Rows.Count > 5000 || next.Rows.Any(r => !double.IsFinite(r.AgeSeconds) || r.AgeSeconds is < 0 or > 315360000 ||
                r.Fields.Length != 16 || r.Fields.Any(f => f == null || f.Length > 4096)))
            throw new InvalidDataException("コンバットログの応答形式または対象プレイヤーが一致しません。");
        if (!next.Available) return ([], next.Reason);
        var overlap = 0;
        string notice = "";
        if (Previous is { } previous)
        {
            if (previous.Epoch != next.Epoch) notice = "サーバーの再起動後のログを追記しました。";
            else
            {
                var elapsed = next.SampleSeconds - previous.SampleSeconds;
                var tolerance = previous.UncertaintySeconds + next.UncertaintySeconds;
                for (var count = Math.Min(previous.Rows.Count, next.Rows.Count); count > 0; count--)
                {
                    var matches = true;
                    for (var n = 0; n < count; n++)
                    {
                        var old = previous.Rows[previous.Rows.Count - count + n]; var current = next.Rows[n];
                        if (!old.Fields.SequenceEqual(current.Fields) || Math.Abs(current.AgeSeconds - old.AgeSeconds - elapsed) > tolerance)
                        { matches = false; break; }
                    }
                    if (matches) { overlap = count; break; }
                }
                if (overlap == 0 && previous.Rows.Count > 0 && next.Rows.Count > 0)
                    notice = "前回からの連続性を確認できません。サーバーに残るログを追記しました。間の記録が欠けている場合があります。";
            }
        }
        var added = next.Rows.Skip(overlap).Select(r => new CombatEntry { OccurredAt = captured.AddSeconds(-r.AgeSeconds), Fields = r.Fields }).ToList();
        Entries.AddRange(added);
        if (Entries.Count > Limit) Entries.RemoveRange(0, Entries.Count - Limit);
        // An empty/delayed response must not make already seen events new again.
        if (next.Rows.Count > 0 || Previous == null || Previous.Epoch != next.Epoch) Previous = next;
        return (added, notice);
    }
}
