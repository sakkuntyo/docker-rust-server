using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using RustMonitor.Core;

namespace RustMonitor;

public partial class MainWindow
{
    private readonly ItemIcons icons;
    private int inventoryGeneration;
    private bool inventoryDetailsVisible = true, inventoryAvailable;

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
        UpdateTeamSelection();
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
            var capacity = key == "main" ? shownInventory?.MainCapacity : key == "belt" ? shownInventory?.BeltCapacity : shownInventory?.WearCapacity;
            var slots = InventoryLayout.Slots(items, key, capacity ?? 0);
            var used = slots.Count(s => s.Item != null);
            var header = new DockPanel { Margin = new Thickness(2, 10, 2, 7) };
            header.Children.Add(new TextBlock { Text = label, FontWeight = FontWeights.SemiBold, FontSize = 13 });
            header.Children.Add(new TextBlock { Text = $"{used} 個", Foreground = Brushes.LightSlateGray, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Right });
            InventoryItems.Children.Add(header);
            var grid = new UniformGrid { Columns = columns, Width = columns * 64, Height = Math.Ceiling((double)slots.Count / columns) * 64 };
            foreach (var slot in slots) grid.Children.Add(InventoryCell(slot));
            InventoryItems.Children.Add(new Viewbox { Stretch = Stretch.Uniform, Child = grid, HorizontalAlignment = HorizontalAlignment.Stretch });
        }
        InventoryItems.Children.Add(new TextBlock { Text = "アイコンにマウスを重ねると詳細を表示\nバックパックなどの中身は右クリック → 開く\n標準アイコン：Facepunch / Rust", FontSize = 10,
            Foreground = Brushes.LightSlateGray, Margin = new Thickness(2, 12, 0, 4), TextWrapping = TextWrapping.Wrap });
    }
    private Border InventoryCell(InventorySlot slot)
    {
        var current = inventoryGeneration;
        var cell = Controls.InventoryItemView.Create(slot, icons, () => !closed && current == inventoryGeneration);
        AddDeleteMenu(cell, slot);
        AddOpenContentsMenu(cell, slot);
        return cell;
    }
}
