using System;
using Rectangle = System.Drawing.Rectangle;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using MyFences.Interop;

namespace MyFences.Views;

internal sealed class SelectionPreviewWindow : Window
{
    private readonly Rectangle _rectangle;
    private readonly ContextMenu _menu = new();
    private readonly MenuItem _create;
    internal event Action<Rectangle>? CreateRequested;

    internal SelectionPreviewWindow(Rectangle rectangle)
    {
        _rectangle = rectangle;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Width = 200; Height = 150;
        var border = new Border
        {
            Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(35, 100, 170, 255)),
            BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(140, 200, 255)),
            BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(5),
            Child = new TextBlock { Text = $"{rectangle.Width} × {rectangle.Height}", Foreground = Brushes.White,
                FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
        };
        Content = border;
        _create = new MenuItem { Header = "Создать блок здесь" };
        _create.Click += (_, _) => CreateRequested?.Invoke(_rectangle);
        _menu.Items.Add(_create);
        var cancel = new MenuItem { Header = "Отмена" };
        cancel.Click += (_, _) => _menu.IsOpen = false;
        _menu.Items.Add(cancel);
        _menu.PlacementTarget = border;
        _menu.Placement = PlacementMode.MousePoint;
        _menu.Closed += (_, _) => Close();
        Loaded += (_, _) =>
        {
            Place();
            Dispatcher.BeginInvoke(new Action(() => _menu.IsOpen = true), DispatcherPriority.Input);
        };
        Closed += (_, _) => _menu.IsOpen = false;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        DesktopHost.Attach(new WindowInteropHelper(this).Handle);
        Place();
    }

    private void Place()
    {
        nint hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != 0) DesktopHost.Place(hwnd, DesktopHost.Find(), _rectangle.X, _rectangle.Y, _rectangle.Width, _rectangle.Height);
    }

    internal void ConfirmForSmoke()
    {
        _create.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        _menu.IsOpen = false;
    }
}
