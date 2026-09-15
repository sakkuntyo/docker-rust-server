using System.Net;

namespace RustMonitor.Core;

public static class IpAttribution
{
    public static void Apply(SshServerSnapshot snapshot, ConnectionReport connections, PresenceSnapshot confirmation)
    {
        foreach (var player in snapshot.Players.Where(p => p.Online))
        {
            player.IpVerified = false;
            var current = confirmation.Players.Where(p => p.SteamId == player.SteamId).ToList();
            if (!confirmation.PresenceAvailable || current.Count != 1 || current[0].Address != player.Address ||
                player.ConnectionSeconds is not double previousSeconds || current[0].ConnectionSeconds is not double nextSeconds ||
                !double.IsFinite(previousSeconds) || !double.IsFinite(nextSeconds) || previousSeconds < 0 || nextSeconds < previousSeconds ||
                !DateTimeOffset.TryParse(player.ObservedAt, out var initialAt) || !DateTimeOffset.TryParse(confirmation.CheckedAt, out var confirmedAt) ||
                confirmedAt < initialAt || Math.Abs(nextSeconds - previousSeconds - (confirmedAt - initialAt).TotalSeconds) > 3)
            { player.IpReason = "照合中に接続が変わったか、再確認できませんでした"; continue; }
            var matches = connections.Matches.Where(m => m.Address == player.Address).ToList();
            if (snapshot.Players.Count(p => p.Online && p.Address == player.Address) != 1 ||
                matches.Count != 1 || matches[0].Status != "verified" || connections.Status != "ok" ||
                !IPAddress.TryParse(matches[0].Ip, out var ip) || !DateTimeOffset.TryParse(connections.CheckedAt, out var checkedAt) ||
                Math.Abs((DateTimeOffset.UtcNow - checkedAt).TotalMinutes) > 3)
            { player.IpReason = matches.Any(m => m.Status == "ambiguous") ? "対応する通信が複数あるため特定できません" : "一致する conntrack の記録がありません"; continue; }
            player.RealIp = ip.ToString(); player.IpCheckedAt = connections.CheckedAt; player.IpVerified = true; player.IpReason = "";
        }
    }
}
