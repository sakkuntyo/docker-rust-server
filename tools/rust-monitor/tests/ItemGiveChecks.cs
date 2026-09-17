using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using RustMonitor;
using RustMonitor.Core;

internal static partial class Program
{
    private static void LiveItemMenuIcons()
    {
        var app = new App(); app.InitializeComponent();
        SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(app.Dispatcher));
        using var db = new Store(Path.Combine(root, "live-item-menu.sqlite3"));
        using var icons = new ItemIcons(Path.Combine(root, "icons"));
        var window = new ItemGiveWindow(db, new SshProfile("admin@demo.invalid", "rust-demo"), CombatSteamId, "Demo Player A", "DEMO / アイテムメニュー", icons,
            (_, _, _) => Task.FromResult(new ItemMenuSnapshot { SteamId = CombatSteamId, Online = true, Items = ItemCatalog.All.ToList() }),
            (_, _, _) => throw new InvalidOperationException("This preview must never give items."));
        window.RefreshCatalogAsync().GetAwaiter().GetResult();
        var content = (FrameworkElement)window.Content; var rows = (ListBox)window.FindName("ItemRows");
        content.Measure(new Size(1432, 868)); content.Arrange(new Rect(0, 0, 1432, 868)); content.UpdateLayout();
        var images = VisualChildren<Image>(rows).ToArray();
        foreach (var image in images) image.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        var frame = new System.Windows.Threading.DispatcherFrame(); var start = DateTime.UtcNow;
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += (_, _) => { if (images.Count(i => i.Source != null) >= 20 || DateTime.UtcNow - start > TimeSpan.FromSeconds(35)) frame.Continue = false; };
        timer.Start(); System.Windows.Threading.Dispatcher.PushFrame(frame); timer.Stop();
        Check(images.Count(i => i.Source != null) >= 8, "live item menu downloads and displays official icons through its asynchronous tile path");
        content.UpdateLayout(); SaveRender(content, "item-menu-live-preview.png", 1432, 868);
        content.Measure(new Size(1032, 668)); content.Arrange(new Rect(0, 0, 1032, 668)); content.UpdateLayout();
        SaveRender(content, "item-menu-live-small.png", 1032, 668);
        window.Close();
    }
    private static void ItemGiveChecks()
    {
        var catalog = ItemCatalog.All;
        Check(catalog.Count > 1200 && catalog.Select(i => i.ItemId).Distinct().Count() == catalog.Count && catalog.Select(i => i.Category).Distinct().Count() == 14,
            "item menu catalog has current unique definitions and all native categories");
        var ak = catalog.Single(i => i.ShortName == "rifle.ak");
        var historical = new ItemRecord { ItemId = 946662961, ShortName = "946662961" }; ItemCatalog.Name(historical);
        Check(historical.Name == "Car Key" && historical.ShortName == "car.key" && !catalog.Any(i => i.ItemId == historical.ItemId),
            "updated item definitions retain names for historical inventories without offering retired items");
        Check(new[] { "ASSAULT", "アサルト", "rifle.ak", ak.ItemId.ToString() }.All(ak.Matches), "item search accepts English, Japanese, shortname, and numeric ID");
        Check(ItemIcons.ImageUri("legacy bow")!.AbsoluteUri.EndsWith("legacy%20bow_512.png"), "legitimate item names with spaces use escaped official icon URLs");
        foreach (var bad in new[] { new GiveItemRequest("７" + CombatSteamId[1..], ak.ItemId, ak.ShortName, 1),
            new GiveItemRequest(CombatSteamId, ak.ItemId, "rifle.ak\nquit", 1), new GiveItemRequest(CombatSteamId, ak.ItemId, ak.ShortName, 10001) })
        {
            try { bad.Validate(); Check(false, "invalid give request"); } catch (ArgumentException) { Check(true, "invalid recipient, command, or quantity is rejected before SSH"); }
        }
        using var db = new Store(Path.Combine(root, "item-menu.sqlite3"));
        var profile = new SshProfile("admin@demo.invalid", "rust-demo");
        using var icons = PrepareRenderIcons(Path.Combine(root, "item-menu"));
        var snapshot = new ItemMenuSnapshot { SteamId = CombatSteamId, Online = true, Items = catalog.ToList() };
        var requests = new List<(SshProfile, GiveItemRequest)>();
        var pending = new TaskCompletionSource<GiveItemResult>();
        var window = new ItemGiveWindow(db, profile, CombatSteamId, "Demo Player A", "DEMO / アイテムメニュー", icons,
            (_, _, _) => Task.FromResult(snapshot), (p, request, _) => { requests.Add((p, request)); return pending.Task; });
        var rows = (ListBox)window.FindName("ItemRows"); var content = (FrameworkElement)window.Content;
        void Layout(int width = 1432) { content.Measure(new Size(width, 868)); content.Arrange(new Rect(0, 0, width, 868)); content.UpdateLayout(); }
        IEnumerable<ItemGiveWindow.ItemChoice> VisibleChoices() => rows.Items.Cast<ItemGiveWindow.ItemTileRow>().SelectMany(r => r.Items);
        void Category(string key) => ((WrapPanel)window.FindName("CategoryTabs")).Children.OfType<ToggleButton>().Single(t => (string)t.Tag == key).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Button Tile(string name) => VisualChildren<Button>(rows).First(b => b.DataContext is ItemGiveWindow.ItemChoice c && c.Item.ShortName == name);
        var give = (Button)window.FindName("GiveButton"); var quantity = (TextBox)window.FindName("QuantityBox");
        var search = (TextBox)window.FindName("SearchBox");
        Layout(); search.Text = "rifle.ak"; Layout(); Tile("rifle.ak").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.GiveSelectedAsync().GetAwaiter().GetResult();
        Check(!give.IsEnabled && requests.Count == 0, "browsing cached definitions and selecting a tile cannot give items");
        window.RefreshCatalogAsync().GetAwaiter().GetResult(); Layout();
        Check(give.IsEnabled && ((TextBox)window.FindName("RecipientText")).Text.Contains(CombatSteamId), "verified online recipient enables the explicit give button");
        quantity.Text = "0"; Check(!give.IsEnabled, "zero quantity disables give");
        quantity.Text = "10001"; Check(!give.IsEnabled, "oversized quantity disables give");
        quantity.Text = "2";
        var star = VisualChildren<ToggleButton>(rows).Single(t => t.DataContext is ItemGiveWindow.ItemChoice c && c.Item.ShortName == "rifle.ak");
        star.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Category("favorites");
        Check(VisibleChoices().Count() == 1 && VisibleChoices().Single().Item.ShortName == "rifle.ak" && requests.Count == 0, "favorites persist without giving or changing the selected recipient");
        var sending = window.GiveSelectedAsync(); window.GiveSelectedAsync().GetAwaiter().GetResult();
        window.Close();
        Check(window.IsSending && !give.IsEnabled && requests.Count == 1 && requests[0] == (profile, new GiveItemRequest(CombatSteamId, ak.ItemId, "rifle.ak", 2)),
            "a pending give captures exact server, player, item and quantity and blocks duplicate sends and closing");
        pending.SetResult(new GiveItemResult { State = "unknown", Message = "付与結果を確認できません。自動再送しません。" }); sending.GetAwaiter().GetResult();
        Check(!window.IsSending && requests.Count == 1 && ((TextBox)window.FindName("StatusText")).Text.Contains("自動再送しません"), "unknown give outcomes are shown and never automatically retried");
        search.Text = ""; Category("*"); Layout();
        Check(VisualChildren<Image>(rows).Count() < catalog.Count / 2, "the game-style catalog virtualizes rows instead of loading all item images at once");
        search.Text = "rifle.ak"; Layout();
        foreach (var image in VisualChildren<Image>(rows)) image.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        Check(VisibleChoices().Any(c => c.Item.ShortName == "rifle.ak" && c.Image != null), "filtered item tiles load their official icon");
        var recycled = VisualChildren<Image>(rows).First();
        var newChoice = new ItemGiveWindow.ItemChoice(ak); recycled.DataContext = newChoice;
        Check(newChoice.Image != null, "recycling a tile loads the new data context image even without another Loaded event");
        recycled.ClearValue(FrameworkElement.DataContextProperty);
        Tile("rifle.ak").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); search.Text = ""; Category("Weapon"); Layout();
        SaveRender(content, "item-menu-preview.png", 1432, 868);
        Layout(1032); SaveRender(content, "item-menu-narrow.png", 1032, 868);
        snapshot.Online = false; window.RefreshCatalogAsync().GetAwaiter().GetResult(); window.GiveSelectedAsync().GetAwaiter().GetResult();
        Check(!give.IsEnabled && requests.Count == 1 && ((TextBox)window.FindName("StatusText")).Text.Contains("オフライン"), "offline recipients can browse but cannot receive an item");
        window.Close();
        var reopened = new ItemGiveWindow(db, profile, CombatSteamId, "Demo", "DEMO", icons, (_, _, _) => Task.FromException<ItemMenuSnapshot>(new IOException("test")));
        var tabs = (WrapPanel)reopened.FindName("CategoryTabs");
        tabs.Children.OfType<ToggleButton>().Single(t => (string)t.Tag == "favorites").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Check(((ListBox)reopened.FindName("ItemRows")).Items.Cast<ItemGiveWindow.ItemTileRow>().SelectMany(r => r.Items).Single().Item.ShortName == "rifle.ak", "favorites are retained on reopening the menu");
        reopened.RefreshCatalogAsync().GetAwaiter().GetResult();
        Check(!((Button)reopened.FindName("GiveButton")).IsEnabled && ((TextBox)reopened.FindName("StatusText")).Text.Contains("付与はできません"), "failed verification leaves cached browsing available and giving disabled");
        reopened.Close();
    }
}
