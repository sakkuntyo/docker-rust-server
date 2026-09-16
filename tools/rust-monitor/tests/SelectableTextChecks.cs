using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using RustMonitor;
using RustMonitor.Controls;

internal static partial class Program
{
    private static IEnumerable<T> VisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var nested in VisualChildren<T>(child)) yield return nested;
        }
    }
    private static string CaptureCopy(TextBox target)
    {
        string captured = "";
        DataObjectCopyingEventHandler handler = (_, e) =>
        {
            captured = (string?)e.DataObject.GetData(DataFormats.UnicodeText) ?? "";
            e.CancelCommand(); // Verify the real Copy command without changing the user's clipboard.
        };
        DataObject.AddCopyingHandler(target, handler);
        try { target.Copy(); } finally { DataObject.RemoveCopyingHandler(target, handler); }
        return captured;
    }
    private static void SelectableTextChecks(MainWindow window)
    {
        var details = (SelectableText)window.FindName("PlayerDetails");
        details.Select(details.Text.IndexOf("76561198000000001", StringComparison.Ordinal), 17);
        Check(CaptureCopy(details) == "76561198000000001", "selected Steam ID copies without the surrounding labels or coordinates");
        var original = details.Text;
        details.SetDisplayText(original + "\n更新済み");
        Check(CaptureCopy(details) == "76561198000000001", "updating other detail text preserves the selected ID for copying");
        details.SetDisplayText(original);
        details.ContextMenu.PlacementTarget = details;
        var copyItem = (MenuItem)details.ContextMenu.Items[0];
        System.Windows.Data.BindingOperations.GetBindingExpression(copyItem, MenuItem.CommandTargetProperty)!.UpdateTarget();
        Check(copyItem.CommandTarget == details, "the context-menu Copy action targets the text that was clicked");
        EditingCommands.Delete.Execute(null, details);
        Check(details.Text == original && details.IsReadOnly, "copyable player information cannot be edited with the delete command");
        var list = (ListBox)window.FindName("PlayerList");
        var row = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(1);
        var id = VisualChildren<SelectableText>(row).Single(t => t.Text.StartsWith("Steam ID:"));
        id.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent });
        id.Select(id.Text.IndexOf("76561198000000002", StringComparison.Ordinal), 17);
        Check(list.SelectedIndex == 1 && CaptureCopy(id) == "76561198000000002" && details.Text.Contains("76561198000000002"),
            "clicking selectable member text still selects the player and supports copying its bound ID");
        list.SelectedIndex = 0;
        var text = new SelectableText { Text = string.Join("\n", Enumerable.Range(0, 30).Select(n => "チャットの行 " + n)) };
        var scroll = new ScrollViewer { Content = text, Width = 260, Height = 60 };
        scroll.Measure(new Size(260, 60)); scroll.Arrange(new Rect(0, 0, 260, 60)); scroll.UpdateLayout();
        text.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, -120) { RoutedEvent = UIElement.PreviewMouseWheelEvent });
        scroll.UpdateLayout();
        Check(scroll.VerticalOffset > 0, "the wheel over selectable text scrolls the surrounding list");
    }
}
