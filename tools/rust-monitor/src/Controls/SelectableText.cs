using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace RustMonitor.Controls;

// Read-only text with native selection/copy, without consuming its list's wheel
// or preventing the clicked player from becoming the selected row.
public class SelectableText : TextBox
{
    public void SetDisplayText(string value)
    {
        var start = SelectionStart;
        var selected = SelectedText;
        Text = value;
        // A timestamp refresh should not interrupt copying an unchanged ID/IP.
        if (selected.Length > 0 && start + selected.Length <= value.Length && value.AsSpan(start, selected.Length).SequenceEqual(selected))
            Select(start, selected.Length);
    }
    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        var row = Ancestor<ListBoxItem>();
        if (row != null) row.SetCurrentValue(ListBoxItem.IsSelectedProperty, true);
        var tableRow = Ancestor<DataGridRow>();
        if (tableRow != null) tableRow.SetCurrentValue(DataGridRow.IsSelectedProperty, true);
        base.OnPreviewMouseLeftButtonDown(e);
    }
    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        if (Ancestor<ScrollViewer>() is ScrollViewer scroll)
        {
            e.Handled = true;
            scroll.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = MouseWheelEvent });
        }
        else base.OnPreviewMouseWheel(e);
    }
    private T? Ancestor<T>() where T : DependencyObject
    {
        for (var parent = VisualTreeHelper.GetParent(this); parent != null; parent = VisualTreeHelper.GetParent(parent))
            if (parent is T result) return result;
        return null;
    }
}
