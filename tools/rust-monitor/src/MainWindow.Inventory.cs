using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Effects;
using RustMonitor.Core;

namespace RustMonitor;

public partial class MainWindow
{
    private readonly ItemIcons icons;
    private int inventoryGeneration;
    private bool inventoryDetailsVisible = true, inventoryAvailable;
    private static readonly Brush DurabilityGood = new SolidColorBrush(Color.FromRgb(145, 187, 81));
    private static readonly Brush DurabilityLow = new SolidColorBrush(Color.FromRgb(207, 112, 77));

    private void InitializeInventory()
    {
        inventoryDetailsVisible = store.Get("inventory-details-visible") != "false";
        UpdateInventoryDetailsVisibility();
    }
    private void InventoryDetailsToggle_Click(object sender, RoutedEventArgs e)
    {
        inventoryDetailsVisible = !inventoryDetailsVisible;
        store.Put("inventory-details-visible", inventoryDetailsVisible ? "true" : "false");
        UpdateInventoryDetailsVisibility();
    }
    private void UpdateInventoryDetailsVisibility()
    {
        InventoryDetailsPanel.Visibility = inventoryDetailsVisible ? Visibility.Visible : Visibility.Collapsed;
        InventoryDetailsToggle.Content = inventoryDetailsVisible ? "▼ 情報を隠す" : "▶ 情報を表示";
        InventoryNotice.Visibility = !inventoryDetailsVisible && !inventoryAvailable ? Visibility.Visible : Visibility.Collapsed;
    }

    private void DrawInventory(List<ItemRecord> items)
    {
        foreach (var (key, label, columns) in new[] { ("main", "インベントリ", 6), ("belt", "ベルト", 6), ("wear", "装備", 8) })
        {
            var slots = InventoryLayout.Slots(items, key);
            var used = slots.Count(s => s.Item != null);
            var header = new DockPanel { Margin = new Thickness(2, 10, 2, 7) };
            header.Children.Add(new TextBlock { Text = label, FontWeight = FontWeights.SemiBold, FontSize = 13 });
            header.Children.Add(new TextBlock { Text = $"{used} 個", Foreground = Brushes.LightSlateGray, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Right });
            InventoryItems.Children.Add(header);
            var grid = new UniformGrid { Columns = columns, Width = columns * 64, Height = Math.Ceiling((double)slots.Count / columns) * 64 };
            foreach (var slot in slots) grid.Children.Add(InventoryCell(slot));
            InventoryItems.Children.Add(new Viewbox { Stretch = Stretch.Uniform, Child = grid, HorizontalAlignment = HorizontalAlignment.Stretch });
        }
        InventoryItems.Children.Add(new TextBlock { Text = "アイコンにマウスを重ねると詳細を表示\n標準アイコン：Facepunch / Rust", FontSize = 10,
            Foreground = Brushes.LightSlateGray, Margin = new Thickness(2, 12, 0, 4), TextWrapping = TextWrapping.Wrap });
    }
    private Border InventoryCell(InventorySlot slot)
    {
        var cell = new Border { Style = (Style)FindResource("InventorySlotStyle"), Tag = slot };
        var panel = new Grid { IsHitTestVisible = false, ClipToBounds = true }; cell.Child = panel;
        panel.Children.Add(new TextBlock { Text = slot.PositionKnown ? (slot.Index + 1).ToString() : "?", FontSize = 10,
            Foreground = new SolidColorBrush(Color.FromRgb(141, 151, 152)), Margin = new Thickness(4, 2, 0, 0), VerticalAlignment = VerticalAlignment.Top });
        if (slot.Item is not ItemRecord item) { cell.ToolTip = $"スロット {slot.Index + 1} • 空"; return cell; }
        ItemCatalog.Name(item);
        var fallback = new TextBlock { Text = item.Name, FontSize = 10, Foreground = Brushes.White, TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap, MaxHeight = 37, Margin = new Thickness(5, 9, 5, 12), VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(fallback);
        var image = new Image { Stretch = Stretch.Uniform, Margin = new Thickness(4, 4, 4, 8) };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality); panel.Children.Add(image);
        panel.Children.Add(new TextBlock { Text = "×" + item.Amount, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 4, 5),
            Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 3, ShadowDepth = 1, Opacity = 1 } });
        if (item.Contents.Count > 0) panel.Children.Add(new TextBlock { Text = "+" + item.Contents.Count, FontSize = 10, Background = new SolidColorBrush(Color.FromArgb(210, 26, 32, 35)),
            Foreground = Brushes.White, Padding = new Thickness(3, 0, 3, 1), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 2, 0) });
        if (item.MaxCondition > 0)
        {
            var fraction = InventoryLayout.Durability(item);
            var track = new Grid { Height = 4, Background = new SolidColorBrush(Color.FromRgb(25, 29, 29)), VerticalAlignment = VerticalAlignment.Bottom };
            track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(fraction, GridUnitType.Star) });
            track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - fraction, GridUnitType.Star) });
            track.Children.Add(new Border { Background = fraction > .2 ? DurabilityGood : DurabilityLow }); panel.Children.Add(track);
        }
        var tooltip = new StackPanel { MaxWidth = 320 };
        tooltip.Children.Add(new TextBlock { Text = item.Name + "   × " + item.Amount, FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        var detail = (slot.PositionKnown ? "スロット " + (slot.Index + 1) : "スロット位置は未確認") + " • " + item.ShortName;
        if (item.MaxCondition > 0) detail += $"\n耐久 {item.Condition:0.#} / {item.MaxCondition:0.#}（{InventoryLayout.Durability(item):P0}）";
        if (item.Ammo != null) detail += "\n装填数 " + item.Ammo;
        if (item.Skin.Length > 0 && item.Skin != "0") detail += "\nスキン ID " + item.Skin + "（画像は標準外観）";
        tooltip.Children.Add(new TextBlock { Text = detail, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.LightSteelBlue, Margin = new Thickness(0, 6, 0, 0) });
        AddInventoryContents(tooltip, item.Contents, 0);
        cell.ToolTip = tooltip;
        ToolTipService.SetInitialShowDelay(cell, 150); ToolTipService.SetShowDuration(cell, 30000);
        System.Windows.Automation.AutomationProperties.SetName(cell, item.Name + " × " + item.Amount + " / " + detail);
        _ = ShowItemIconAsync(image, fallback, item.ShortName, inventoryGeneration);
        AddDeleteMenu(cell, slot);
        return cell;
    }
    private static void AddInventoryContents(Panel panel, List<ItemRecord> contents, int depth)
    {
        if (depth >= 6 || contents.Count == 0) return;
        if (depth == 0) panel.Children.Add(new TextBlock { Text = "アタッチメント / 内容物", Margin = new Thickness(0, 10, 0, 4), FontWeight = FontWeights.SemiBold });
        foreach (var item in contents)
        {
            ItemCatalog.Name(item);
            var text = item.Name + " × " + item.Amount;
            if (item.MaxCondition > 0) text += $" • 耐久 {item.Condition:0.#}/{item.MaxCondition:0.#}";
            if (item.Ammo != null) text += " • 装填 " + item.Ammo;
            panel.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(depth * 10, 2, 0, 0) });
            AddInventoryContents(panel, item.Contents, depth + 1);
        }
    }
    private async Task ShowItemIconAsync(Image image, TextBlock fallback, string shortName, int current)
    {
        var bitmap = await icons.GetAsync(shortName);
        if (closed || current != inventoryGeneration || bitmap == null) return;
        image.Source = bitmap; fallback.Visibility = Visibility.Collapsed;
    }
}
