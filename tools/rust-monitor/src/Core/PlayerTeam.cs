using System.IO;

namespace RustMonitor.Core;

public sealed class TeamMember
{
    public string SteamId { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Online { get; set; }
    public bool Leader { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public string Label => (Leader ? "★ " : "") + (string.IsNullOrWhiteSpace(Name) ? "名前未確認" : Name.Replace('\r', ' ').Replace('\n', ' ')) + (Leader ? "（リーダー）" : "");
}

public sealed class PlayerTeam
{
    public string SteamId { get; set; } = "";
    public string State { get; set; } = "";
    public string CheckedAt { get; set; } = "";
    public List<TeamMember> Members { get; set; } = [];
    public void Validate(string steamId)
    {
        if (SteamId != steamId || !DateTimeOffset.TryParse(CheckedAt, out _) || State is not ("members" or "none" or "unavailable") ||
            Members == null || Members.Count > 128 || Members.Any(m => m == null || m.SteamId.Length != 17 || !m.SteamId.All(char.IsAsciiDigit)) ||
            Members.Select(m => m.SteamId).Distinct().Count() != Members.Count ||
            (State == "members" ? !Members.Any(m => m.SteamId == steamId) : Members.Count != 0))
            throw new InvalidDataException("パーティの取得結果が一致しません。");
    }
    public string Heading => State == "members" ? $"パーティ（ゲーム内チーム） • {Members.Count} 人" : State == "none" ? "パーティ：所属なし" : "パーティ：サーバー上にプレイヤーの記録がありません";
    public string MemberText => string.Join("\n", Members.OrderByDescending(m => m.Leader).Select(m =>
        m.Label + " • " + (m.Online ? "オンライン" : "オフライン") + "\nSteam ID: " + m.SteamId));
}
