using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Navigation;
using RustMonitor;
using RustMonitor.Core;

internal static partial class Program
{
    private static void PlayerLinkChecks(MainWindow window, List<Uri> opened)
    {
        var steam = (Hyperlink)window.FindName("SteamProfileLink");
        var ip = (Hyperlink)window.FindName("IpInfoLink");
        var ipHost = (FrameworkElement)window.FindName("IpInfoLinkHost");
        var list = (ListBox)window.FindName("PlayerList");
        void Click(Hyperlink link) => link.RaiseEvent(new RequestNavigateEventArgs(link.NavigateUri!, "") { RoutedEvent = Hyperlink.RequestNavigateEvent });
        Check(opened.Count == 0, "loading and switching player details never opens an external page automatically");
        Click(steam); Click(ip);
        Check(opened.Select(u => u.AbsoluteUri).SequenceEqual(new[] {
            "https://steamcommunity.com/profiles/76561198000000001", "https://ipinfo.io/203.0.113.10" }),
            "activating the player links opens the selected Steam profile and confirmed IP in the browser");
        var ipText = (TextBox)window.FindName("PlayerIp");
        ipText.Select(ipText.Text.IndexOf("203.0.113.10", StringComparison.Ordinal), "203.0.113.10".Length);
        Check(CaptureCopy(ipText) == "203.0.113.10" && ipText.Text.Contains("最終確認"), "the historical IP remains clearly labeled and copyable beside its link");
        var content = (FrameworkElement)window.Content; content.UpdateLayout();
        var steamText = (FrameworkElement)window.FindName("PlayerSteamId");
        var steamHost = (FrameworkElement)window.FindName("SteamProfileLinkHost");
        var steamPosition = steamHost.TranslatePoint(new Point(), content);
        var idPosition = steamText.TranslatePoint(new Point(), content);
        var ipPosition = ipHost.TranslatePoint(new Point(), content);
        var ipTextPosition = ipText.TranslatePoint(new Point(), content);
        Check(Math.Abs(steamPosition.Y - idPosition.Y) < 1 && steamPosition.X >= idPosition.X + steamText.ActualWidth &&
            Math.Abs(ipPosition.Y - ipTextPosition.Y) < 1 && ipPosition.X >= ipTextPosition.X + ipText.ActualWidth,
            "both links sit beside their matching text at the normal window width");
        list.SelectedIndex = 1;
        Click(steam);
        Check(opened[^1].AbsoluteUri.EndsWith("/76561198000000002") && ip.NavigateUri == null && ipHost.Visibility == Visibility.Collapsed,
            "switching players updates the profile destination and removes a previous player's IP link when the new IP is unknown");
        list.SelectedIndex = -1;
        Check(steam.NavigateUri == null && ip.NavigateUri == null && ((TextBox)window.FindName("PlayerSteamId")).Text.Length == 0,
            "clearing the selection also clears both link destinations");
        var record = new PlayerRecord { SteamId = "../../unexpected", RealIp = "203.0.113.10/other", IpCheckedAt = "2026-09-17T00:00:00Z" };
        var row = new MainWindow.PlayerRow(record, false);
        Check(row.SteamProfileUri == null && row.IpInfoUri == null, "malformed Steam IDs and IP addresses do not create external links");
        record.RealIp = "203.0.113.10"; record.IpCheckedAt = "";
        Check(row.IpInfoUri == null, "an IP without a recorded confirmation does not create a lookup link");
        record.RealIp = "2001:db8::1"; record.IpCheckedAt = "2026-09-17T00:00:00Z";
        Check(row.IpInfoUri?.Host == "ipinfo.io" && Uri.UnescapeDataString(row.IpInfoUri.AbsolutePath) == "/2001:db8::1",
            "confirmed IPv6 addresses produce a valid IPinfo page path");
        list.SelectedIndex = 0;
    }
}
