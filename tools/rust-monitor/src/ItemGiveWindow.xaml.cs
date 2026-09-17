using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using RustMonitor.Core;

namespace RustMonitor;

public partial class ItemGiveWindow : Window
{
    private readonly Store store;
    private readonly SshProfile profile;
    private readonly string steamId, playerName;
    private readonly ItemIcons icons;
    private readonly Func<SshProfile, string, CancellationToken, Task<ItemMenuSnapshot>> reader;
    private readonly Func<SshProfile, GiveItemRequest, CancellationToken, Task<GiveItemResult>> giver;
    private readonly CancellationTokenSource lifetime = new();
    private readonly HashSet<int> favorites;
    private List<ItemChoice> choices = [];
    private ItemChoice? selected;
    private string category = "Weapon";
    private int columns = 8;
    private bool ready, closed, loading, verified, online;
    public bool IsSending { get; private set; }
    public static readonly (string Key, string Label)[] Categories = [("*", "すべて"), ("favorites", "★ お気に入り"), ("Misc", "その他"), ("Items", "アイテム"),
        ("Ammunition", "弾薬"), ("Weapon", "武器"), ("Electrical", "電気"), ("Fun", "娯楽"), ("Component", "コンポーネント"), ("Tool", "ツール"),
        ("Traps", "トラップ"), ("Construction", "建築"), ("Food", "食料"), ("Medical", "医療"), ("Resources", "資源"), ("Attire", "装備")];

    public ItemGiveWindow(Store store, SshProfile profile, string steamId, string playerName, string serverName, ItemIcons icons,
        Func<SshProfile, string, CancellationToken, Task<ItemMenuSnapshot>>? reader = null,
        Func<SshProfile, GiveItemRequest, CancellationToken, Task<GiveItemResult>>? giver = null)
    {
        InitializeComponent(); this.store = store; this.profile = profile; this.steamId = steamId; this.playerName = playerName; this.icons = icons;
        this.reader = reader ?? DockerSsh.ReadItemMenuAsync; this.giver = giver ?? DockerSsh.GiveItemAsync;
        Title = "Rust Monitor / ＋アイテム / " + playerName;
        RecipientText.Text = "付与先：" + playerName + " • Steam ID: " + steamId;
        DestinationText.Text = serverName + " • " + profile.Target + " / " + profile.Container;
        favorites = store.Get("item-favorites") is string json ? Wire.Read<HashSet<int>>(json) : [];
        foreach (var (key, label) in Categories)
        {
            var button = new ToggleButton { Content = label, Tag = key, Style = (Style)FindResource("CategoryTab") };
            button.Click += (_, _) => { category = key; Filter(); };
            CategoryTabs.Children.Add(button);
        }
        var cached = store.Get("item-catalog:" + profile.Key);
        SetCatalog(cached != null ? Wire.Read<List<CatalogItem>>(cached) : ItemCatalog.All);
        ready = true; Filter(); UpdateControls();
        Loaded += async (_, _) => await RefreshCatalogAsync();
        Closing += (_, e) => { if (IsSending) { e.Cancel = true; StatusText.Text = "付与結果を確認しています。完了後に閉じてください。"; } };
        Closed += (_, _) => { closed = true; lifetime.Cancel(); lifetime.Dispose(); };
    }
    private void SetCatalog(IEnumerable<CatalogItem> items)
    {
        var id = selected?.Item.ItemId;
        choices = items.Select(i => new ItemChoice(i) { Favorite = favorites.Contains(i.ItemId) }).OrderBy(i => i.Item.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
        Select(choices.FirstOrDefault(i => i.Item.ItemId == id));
    }
    public async Task RefreshCatalogAsync()
    {
        if (closed || loading || IsSending) return;
        loading = true; verified = false; UpdateControls(); StatusText.Text = "対象サーバーのアイテム一覧・接続状態を確認しています…";
        try
        {
            var result = await reader(profile, steamId, lifetime.Token);
            if (closed) return;
            if (result.SteamId != steamId || result.Items.Count is < 1 or > 10000 || result.Items.Any(i => i.ItemId == 0 || !GiveItemRequest.ValidShortName(i.ShortName) || i.StackSize < 1))
                throw new System.IO.InvalidDataException("アイテム一覧または対象プレイヤーが一致しません。");
            SetCatalog(result.Items); store.Put("item-catalog:" + profile.Key, Wire.Write(result.Items));
            verified = true; online = result.Online; Filter();
            StatusText.Text = online ? "接続中 • アイテムを選び、数量を指定してください。" : "対象プレイヤーはオフラインです。一覧の閲覧はできます。接続後に「一覧を更新」してください。";
        }
        catch (Exception ex) { if (!closed) StatusText.Text = "一覧を確認できません。保存済みの一覧を表示しています。付与はできません。\n" + ex.Message; }
        finally { loading = false; if (!closed) UpdateControls(); }
    }
    private void Filter()
    {
        if (!ready) return;
        foreach (var tab in CategoryTabs.Children.OfType<ToggleButton>()) tab.IsChecked = (string)tab.Tag == category;
        var filtered = choices.Where(c => (category == "*" || category == "favorites" && c.Favorite || category == c.Item.Category ||
            category == "Misc" && !Categories.Any(k => k.Key == c.Item.Category)) && c.Item.Matches(SearchBox.Text)).ToList();
        if (selected != null && !filtered.Contains(selected)) Select(null);
        ItemRows.ItemsSource = filtered.Chunk(columns).Select(items => new ItemTileRow(items, columns)).ToList();
        CountText.Text = filtered.Count + " / " + choices.Count + " アイテム • ★ " + favorites.Count;
        EmptyText.Visibility = filtered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private void Select(ItemChoice? choice)
    {
        if (selected != null) selected.Selected = false;
        selected = choice;
        if (selected != null) selected.Selected = true;
        SelectedName.Text = choice?.Item.DisplayName ?? "アイテムを選択してください";
        SelectedDetail.Text = choice == null ? "" : choice.Item.ShortName + " • ID: " + choice.Item.ItemId + "\n標準スタック数 " + choice.Item.StackSize;
        SelectedImage.Source = choice?.Image; UpdateControls();
    }
    private bool ValidAmount(out int amount) => int.TryParse(QuantityBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out amount) && amount is >= 1 and <= GiveItemRequest.MaximumAmount;
    private void UpdateControls()
    {
        if (!ready) return;
        ReloadButton.IsEnabled = !loading && !IsSending;
        SearchTools.IsEnabled = CategoryTabs.IsEnabled = ItemRows.IsEnabled = QuantityTools.IsEnabled = !IsSending;
        GiveButton.IsEnabled = verified && online && selected != null && !loading && !IsSending && ValidAmount(out _);
        StackButton.IsEnabled = selected != null;
        QuantityBox.ToolTip = ValidAmount(out _) ? "1～10,000" : "数量は1～10,000の整数で入力してください。";
    }
    private void Item_Click(object sender, RoutedEventArgs e) { if (!IsSending && sender is FrameworkElement { DataContext: ItemChoice choice }) Select(choice); }
    private void Favorite_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (IsSending || sender is not FrameworkElement { DataContext: ItemChoice choice }) return;
        if (!favorites.Add(choice.Item.ItemId)) favorites.Remove(choice.Item.ItemId);
        choice.Favorite = favorites.Contains(choice.Item.ItemId); store.Put("item-favorites", Wire.Write(favorites)); Filter();
    }
    private void ItemImage_Loaded(object sender, RoutedEventArgs e) => LoadImage(sender);
    private void ItemImage_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e) => LoadImage(sender);
    private async void LoadImage(object sender)
    {
        if (sender is not Image { DataContext: ItemChoice choice } || choice.Image != null || choice.ImageRequested) return;
        choice.ImageRequested = true;
        var image = await icons.GetAsync(choice.Item.ShortName);
        if (closed) return;
        choice.Image = image; if (ReferenceEquals(selected, choice)) SelectedImage.Source = image;
    }
    private void ItemRows_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var count = Math.Max(1, (int)Math.Floor((ItemRows.ActualWidth - 8) / 150));
        if (columns != count) { columns = count; Filter(); }
    }
    private void Search_Changed(object sender, TextChangedEventArgs e) => Filter();
    private void Quantity_Changed(object sender, TextChangedEventArgs e) => UpdateControls();
    private void QuantityPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        QuantityBox.Text = (string)button.Tag == "stack" ? Math.Min(selected?.Item.StackSize ?? 1, GiveItemRequest.MaximumAmount).ToString() : (string)button.Tag;
    }
    private async void Reload_Click(object sender, RoutedEventArgs e) => await RefreshCatalogAsync();
    private async void Give_Click(object sender, RoutedEventArgs e) => await GiveSelectedAsync();
    public async Task GiveSelectedAsync()
    {
        if (closed || IsSending || loading || !verified || !online || selected == null || !ValidAmount(out var amount)) return;
        var item = selected.Item;
        var request = new GiveItemRequest(steamId, item.ItemId, item.ShortName, amount); request.Validate();
        IsSending = true; UpdateControls();
        var summary = playerName + " に " + item.DisplayName + " × " + amount;
        StatusText.Text = summary + " を付与しています…";
        GiveItemResult result;
        try { result = await giver(profile, request, CancellationToken.None); }
        catch (Exception) { result = new() { State = "unknown", Message = "付与結果を確認できません。自動再送しません。再実行する前にゲーム内の所持品を確認してください。" }; }
        finally { IsSending = false; if (!closed) UpdateControls(); }
        if (closed) return;
        StatusText.Text = summary + " • " + result.Message;
    }
    public sealed record ItemTileRow(ItemChoice[] Items, int Columns);
    public sealed class ItemChoice(CatalogItem item) : INotifyPropertyChanged
    {
        public CatalogItem Item => item;
        public string Detail => item.DisplayName + " / " + item.Name + "\n" + item.ShortName + " • ID: " + item.ItemId + "\n標準スタック数 " + item.StackSize;
        private bool selected, favorite;
        private ImageSource? image;
        public bool ImageRequested { get; set; }
        public bool Selected { get => selected; set { selected = value; PropertyChanged?.Invoke(this, new(nameof(Selected))); } }
        public bool Favorite { get => favorite; set { favorite = value; PropertyChanged?.Invoke(this, new(nameof(Favorite))); } }
        public ImageSource? Image { get => image; set { image = value; PropertyChanged?.Invoke(this, new(nameof(Image))); } }
        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
