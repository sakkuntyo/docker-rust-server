using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using RustMonitor.Core;

namespace RustMonitor;

public partial class MainWindow
{
    private readonly MapViewport mapView = new();
    private string loadedMapKey = "";
    private Point? dragPoint;
    private static readonly Brush MapLabelBrush = new SolidColorBrush(Color.FromArgb(225, 16, 24, 32));

    private Point? MapPoint(PlayerRecord player) => MapImage.Source is BitmapSource bitmap && server != null
        ? MapViewport.Project(player, server.WipeId, server.Size, bitmap.PixelWidth, bitmap.PixelHeight) : null;

    private void UpdateMapLayout()
    {
        if (MapMarkers == null) return;
        MapMarkers.Children.Clear();
        var ready = MapImage.Source is BitmapSource;
        MapZoomIn.IsEnabled = ready && mapView.Zoom < 16;
        MapZoomOut.IsEnabled = ready && mapView.Zoom > 1;
        MapReset.IsEnabled = ready;
        MapFocus.IsEnabled = PlayerList.SelectedItem is PlayerRow row && MapPoint(row.Record) != null;
        MapZoomText.Text = $"{mapView.Zoom * 100:0}%";
        if (MapImage.Source is not BitmapSource bitmap) { MapPositionStatus.Text = "座標は最終セーブ時点の記録です。"; return; }
        mapView.Resize(new Size(MapArea.ActualWidth, MapArea.ActualHeight), new Size(bitmap.PixelWidth, bitmap.PixelHeight));
        var rect = mapView.ImageRect;
        if (rect.IsEmpty) return;
        MapImage.Width = rect.Width; MapImage.Height = rect.Height;
        Canvas.SetLeft(MapImage, rect.X); Canvas.SetTop(MapImage, rect.Y);
        var selected = (PlayerList.SelectedItem as PlayerRow)?.SteamId;
        var positioned = PlayerList.Items.Cast<PlayerRow>().Select(p => (Player: p, Point: MapPoint(p.Record)))
            .Where(p => p.Point != null).OrderBy(p => p.Player.SteamId == selected).ToList();
        var visible = 0;
        var labels = new List<Rect>();
        // Place the selected label first so that crowded labels cannot cover it.
        var markerLabels = new List<(PlayerRow Player, Point Point, bool Selected)>();
        foreach (var entry in positioned)
        {
            var point = mapView.ToScreen(entry.Point!.Value);
            if (point.X < 0 || point.Y < 0 || point.X > MapArea.ActualWidth || point.Y > MapArea.ActualHeight) continue;
            visible++;
            var player = entry.Player; var isSelected = player.SteamId == selected;
            var color = isSelected ? Brushes.Orange : player.IsOnline ? Brushes.MediumSpringGreen : Brushes.LightSlateGray;
            var button = new Button { Width = 20, Height = 20, Padding = new Thickness(0), Margin = new Thickness(0), Background = Brushes.Transparent,
                Content = new Ellipse { Width = isSelected ? 16 : 12, Height = isSelected ? 16 : 12, Fill = color, Stroke = Brushes.White, StrokeThickness = isSelected ? 2 : 1 },
                ToolTip = player.Name + " • " + player.State + "\n" + Coordinates(player.Record) + "\nセーブ " + Time(player.Record.PositionAt), Tag = player.SteamId };
            System.Windows.Automation.AutomationProperties.SetName(button, (string)button.ToolTip);
            button.Click += MapPlayer_Click;
            Canvas.SetLeft(button, point.X - 10); Canvas.SetTop(button, point.Y - 10); Panel.SetZIndex(button, isSelected ? 4 : 2);
            MapMarkers.Children.Add(button);
            markerLabels.Add((player, point, isSelected));
        }
        foreach (var (player, point, isSelected) in markerLabels.OrderByDescending(p => p.Selected))
        {
            if (MapNames.IsChecked != true && !isSelected) continue;
            var label = new TextBlock { Text = player.Name + $"\nX {player.Record.X:0} / Z {player.Record.Z:0}", MaxWidth = 150,
                TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 10, Foreground = isSelected ? Brushes.Orange : Brushes.White,
                Padding = new Thickness(5, 2, 5, 2), Background = MapLabelBrush, IsHitTestVisible = false };
            label.Measure(new Size(150, double.PositiveInfinity));
            var area = new Rect(Math.Clamp(point.X + 11, 0, Math.Max(0, MapArea.ActualWidth - label.DesiredSize.Width)),
                Math.Clamp(point.Y - label.DesiredSize.Height / 2, 0, Math.Max(0, MapArea.ActualHeight - label.DesiredSize.Height)), label.DesiredSize.Width, label.DesiredSize.Height);
            if (!isSelected && labels.Any(p => p.IntersectsWith(area))) continue;
            labels.Add(area); Canvas.SetLeft(label, area.X); Canvas.SetTop(label, area.Y); Panel.SetZIndex(label, isSelected ? 3 : 1);
            MapMarkers.Children.Add(label);
        }
        var outside = PlayerList.Items.Cast<PlayerRow>().Count(p => p.Record.WipeId == server?.WipeId &&
            p.Record.X != null && p.Record.Z != null && p.Record.PositionAt.Length > 0 && MapPoint(p.Record) == null);
        MapPositionStatus.Text = $"マップ内 {positioned.Count} 人 / 画面内 {visible} 人" + (outside > 0 ? $" / 画像外 {outside} 人" : "") +
            $"（一覧の条件に連動）\n最終セーブ {Time(server?.SaveAt ?? "")} の位置";
    }

    private static string Coordinates(PlayerRecord player) => $"X {player.X:0.0} / Y {player.Y:0.0} / Z {player.Z:0.0}";
    private void MapPlayer_Click(object sender, RoutedEventArgs e)
    {
        var id = ((Button)sender).Tag as string;
        PlayerList.SelectedItem = PlayerList.Items.Cast<PlayerRow>().FirstOrDefault(p => p.SteamId == id);
        if (PlayerList.SelectedItem != null) PlayerList.ScrollIntoView(PlayerList.SelectedItem);
        e.Handled = true;
    }
    private void MapArea_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateMapLayout();
        if (ChatBody != null) ChatBody.Height = Math.Clamp(MapArea.ActualHeight * .53, 150, 300);
    }
    private void MapNames_Changed(object sender, RoutedEventArgs e) => UpdateMapLayout();
    private void ZoomMap(double factor, Point? anchor = null)
    {
        mapView.ZoomAt(factor, anchor ?? new Point(MapArea.ActualWidth / 2, MapArea.ActualHeight / 2)); UpdateMapLayout();
    }
    private void MapZoomIn_Click(object sender, RoutedEventArgs e) => ZoomMap(1.5);
    private void MapZoomOut_Click(object sender, RoutedEventArgs e) => ZoomMap(1 / 1.5);
    private void MapReset_Click(object sender, RoutedEventArgs e) { mapView.Reset(); UpdateMapLayout(); }
    private void MapFocus_Click(object sender, RoutedEventArgs e)
    {
        if (PlayerList.SelectedItem is PlayerRow player && MapPoint(player.Record) is Point point) { mapView.Focus(point); UpdateMapLayout(); }
    }
    private void MapArea_Wheel(object sender, MouseWheelEventArgs e)
    {
        if (MapImage.Source == null) return;
        ZoomMap(Math.Pow(1.2, e.Delta / 120.0), e.GetPosition(MapArea)); e.Handled = true;
    }
    private void MapArea_Down(object sender, MouseButtonEventArgs e)
    {
        if (MapImage.Source == null) return;
        // Button presses select a player; background presses pan the map.
        for (var element = e.OriginalSource as DependencyObject; element != null && element != MapArea; element = VisualTreeHelper.GetParent(element))
            if (element is Button) return;
        dragPoint = e.GetPosition(MapArea); MapArea.CaptureMouse(); MapArea.Cursor = Cursors.SizeAll; e.Handled = true;
    }
    private void MapArea_Move(object sender, MouseEventArgs e)
    {
        if (dragPoint is not Point previous || e.LeftButton != MouseButtonState.Pressed) return;
        var next = e.GetPosition(MapArea); mapView.Pan(next - previous); dragPoint = next; UpdateMapLayout();
    }
    private void MapArea_Up(object sender, MouseButtonEventArgs e) { if (dragPoint != null) { MapArea.ReleaseMouseCapture(); e.Handled = true; } }
    private void MapArea_LostCapture(object sender, MouseEventArgs e) { dragPoint = null; MapArea.Cursor = Cursors.Arrow; }
}
