using System.IO;

namespace RustMonitor.Core;

public sealed class ChatMessage
{
    public int Channel { get; set; }
    public string Message { get; set; } = "";
    public string UserId { get; set; } = "";
    public string Username { get; set; } = "";
    public long Time { get; set; }
    public string TimeText => DateTimeOffset.FromUnixTimeSeconds(Time).ToLocalTime().ToString("MM/dd HH:mm:ss");
    public string ChannelText => Channel switch { 0 => "全体", 1 => "チーム", 2 => "サーバー", 3 => "カード", 4 => "周辺", 5 => "クラン", 6 => "DM", _ => "その他" };
    public string Heading => $"{TimeText}  [{ChannelText}]  {Username}";
}

public sealed class ChatSnapshot
{
    public string CheckedAt { get; set; } = "";
    public List<ChatMessage> Messages { get; set; } = [];
}

public sealed class ChatSendResult
{
    public bool Accepted { get; set; }
}

public static class ChatText
{
    public static string Validate(string value)
    {
        if (value.Any(c => char.IsControl(c) || c is '\u2028' or '\u2029'))
            throw new ArgumentException("改行や制御文字を含めず、1行で入力してください。");
        value = value.Trim();
        if (value.Length is < 1 or > 256) throw new ArgumentException("発言は1～256文字で入力してください。");
        return value;
    }
}
