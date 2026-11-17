using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using MyFences.Interop;
using MyFences.Models;
using MyFences.Services;

namespace MyFences.Views;

public partial class FenceWindow : Window
{
    internal FenceState State { get; }
    internal nint Handle { get; private set; }
    internal nint ContentHandle { get; private set; }
    internal nint Host { get; private set; }
    internal bool IsClosed { get; private set; }
    internal event Action? Changed;
    internal event Action<FenceWindow>? RemoveRequested;
    private readonly ObservableCollection<FenceItem> _items = new();
    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _hoverCollapse = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private bool _hoverExpanded, _menuOpen;
    private bool IsVisuallyRolled => State.RolledUp && !_hoverExpanded;
    private FileSystemWatcher? _watcher;
    private bool _moving, _placing, _quickHidden, _importing, _loadingItems, _reloadItems, _boundsReady;
    private Native.Point _startMouse;
    private Native.Rect _startRect;
    private double Scale => VisualTreeHelper.GetDpi(this).DpiScaleX;
    private int RolledHeight => (int)Math.Ceiling(44 * Scale);
    // Keep the WPF window top-level; Explorer is its owner, not its parent.
    private static readonly DependencyProperty DesktopOpacityProperty = DependencyProperty.Register(
        "DesktopOpacity", typeof(double), typeof(FenceWindow),
        new PropertyMetadata(0.94, (owner, _) => ((FenceWindow)owner).ApplyNativeOpacity()));
    private double DesktopOpacity
    {
        get => (double)GetValue(DesktopOpacityProperty);
        set => SetValue(DesktopOpacityProperty, value);
    }

    private void ApplyNativeOpacity()
    {
        if (IsClosed) return;
        Opacity = Math.Clamp(DesktopOpacity / 0.94, 0, 1);
    }
    internal int DisplayedItemCount => _items.Count;
    internal bool IsImporting => _importing;
    internal double DpiScaleForDiagnostics => Scale;

    internal void ExerciseRollForSmoke()
    {
        int originalHeight = State.Height;
        if (State.RolledUp) ToggleRoll();
        ToggleRoll();
        Native.GetWindowRect(Handle, out var rolled);
        if (Math.Abs(rolled.Bottom - rolled.Top - RolledHeight) > 2) throw new InvalidOperationException("Неверная высота свёрнутого блока.");
        SetHoverExpanded(true);
        Native.GetWindowRect(Handle, out var preview);
        if (!State.RolledUp || Math.Abs(preview.Bottom - preview.Top - originalHeight) > 2) throw new InvalidOperationException("Наведение не раскрыло свёрнутый блок.");
        SetHoverExpanded(false);
        Native.GetWindowRect(Handle, out var restored);
        if (!State.RolledUp || Math.Abs(restored.Bottom - restored.Top - RolledHeight) > 2) throw new InvalidOperationException("После ухода мыши блок не свернулся.");
        ToggleRoll();
        Native.GetWindowRect(Handle, out var expanded);
        if (Math.Abs(expanded.Bottom - expanded.Top - originalHeight) > 2) throw new InvalidOperationException("Не восстановлена высота блока.");
    }

    internal FenceWindow(FenceState state)
    {
        State = state;
        InitializeComponent();
        TitleText.Text = state.Title;
        ItemsList.ItemsSource = _items;
        MouseEnter += (_, _) => { _hoverCollapse.Stop(); SetHoverExpanded(true); };
        MouseLeave += (_, _) => { if (State.RolledUp && _hoverExpanded) _hoverCollapse.Start(); };
        DragEnter += (_, _) => { _hoverCollapse.Stop(); SetHoverExpanded(true); };
        _hoverCollapse.Tick += (_, _) =>
        {
            if (_moving || _menuOpen || _importing || TitleEditor.IsKeyboardFocusWithin) return;
            _hoverCollapse.Stop();
            if (!IsMouseOver) SetHoverExpanded(false);
        };
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(280) };
        _refreshTimer.Tick += async (_, _) => { _refreshTimer.Stop(); await ReloadAsync(); };
        DragLeave += (_, _) => DropOverlay.Visibility = Visibility.Collapsed;
        Loaded += async (_, _) =>
        {
            ApplyBounds();
            _boundsReady = true;
            ApplyNativeOpacity();
            if (!_quickHidden) Native.ShowWindow(Handle, 4);
            StartWatcher();
            await ReloadAsync();
        };
        Closed += (_, _) =>
        {
            IsClosed = true;
            _refreshTimer.Stop();
            _hoverCollapse.Stop();
            _watcher?.Dispose();
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ContentHandle = new WindowInteropHelper(this).Handle;
        Handle = ContentHandle;
        Host = DesktopHost.Attach(Handle);
        HwndSource.FromHwnd(ContentHandle)?.AddHook(WindowMessage);
        ApplyBounds();
    }

    private nint WindowMessage(nint hwnd, int message, nint wp, nint lp, ref bool handled)
    {
        // WM_WINDOWPOSCHANGED: сохраняем реальные экранные координаты,
        // а не Window.Left/Top дочернего WPF-окна.
        if (message == 0x47 && !_placing && _boundsReady && !_moving) CaptureBounds();
        if (message == 0x2E0)
            Dispatcher.BeginInvoke(new Action(ApplyBounds), DispatcherPriority.Loaded);
        return 0;
    }

    internal void Reattach()
    {
        Host = DesktopHost.Attach(Handle);
        ApplyBounds();
        ApplyNativeOpacity();
    }

    private void ApplyBounds()
    {
        if (Handle == 0 || IsClosed || !Native.IsWindow(Handle)) return;
        _placing = true;
        try
        {
            Body.Visibility = Footer.Visibility = IsVisuallyRolled ? Visibility.Collapsed : Visibility.Visible;
            RollButton.Content = State.RolledUp ? "⌄" : "⌃";
            int height = IsVisuallyRolled ? RolledHeight : State.Height;
            var desired = new System.Drawing.Rectangle(State.X, State.Y, State.Width, height);
            var screen = _moving && Native.GetCursorPos(out var cursor)
                ? System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(cursor.X, cursor.Y))
                : System.Windows.Forms.Screen.FromRectangle(desired);
            var bounded = DesktopBounds.Clamp(desired, screen.WorkingArea);
            if (!_hoverExpanded || _moving) { State.X = bounded.X; State.Y = bounded.Y; }
            State.Width = bounded.Width;
            State.Height = State.RolledUp ? Math.Min(State.Height, screen.WorkingArea.Height) : bounded.Height;
            height = bounded.Height;
            DesktopHost.Place(Handle, Host, bounded.X, bounded.Y, State.Width, height);
        }
        finally { _placing = false; }
    }

    internal void CaptureBounds()
    {
        if (Handle == 0 || IsClosed || !_boundsReady || _placing || (_hoverExpanded && !_moving) || !Native.GetWindowRect(Handle, out var r)) return;
        int width = r.Right - r.Left, height = r.Bottom - r.Top;
        if (width < 100 || height < 20) return;
        if (State.X == r.Left && State.Y == r.Top && State.Width == width && (State.RolledUp || State.Height == height)) return;
        State.X = r.Left; State.Y = r.Top; State.Width = width;
        if (!State.RolledUp) State.Height = height;
        Changed?.Invoke();
    }

    internal void KeepWithinScreen()
    {
        if (!IsLoaded || IsClosed || _moving || _placing || _hoverExpanded) return;
        var rectangle = new System.Drawing.Rectangle(State.X, State.Y, State.Width, IsVisuallyRolled ? RolledHeight : State.Height);
        var area = System.Windows.Forms.Screen.FromRectangle(rectangle).WorkingArea;
        if (DesktopBounds.Clamp(rectangle, area) == rectangle) return;
        ApplyBounds(); Changed?.Invoke();
    }

    private void Header_Down(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<Button>(e.OriginalSource as DependencyObject) != null ||
            FindAncestor<TextBox>(e.OriginalSource as DependencyObject) != null) return;
        if (e.ClickCount == 2) { ToggleRoll(); e.Handled = true; return; }
        Native.GetCursorPos(out _startMouse);
        Native.GetWindowRect(Handle, out _startRect);
        _moving = true;
        Header.CaptureMouse();
        e.Handled = true;
    }

    private void Header_Move(object sender, MouseEventArgs e)
    {
        if (!_moving || e.LeftButton != MouseButtonState.Pressed) return;
        Native.GetCursorPos(out var now);
        State.X = _startRect.Left + now.X - _startMouse.X;
        State.Y = _startRect.Top + now.Y - _startMouse.Y;
        ApplyBounds();
    }

    private void Header_Up(object sender, MouseButtonEventArgs e) => EndMove();
    private void Header_LostCapture(object sender, MouseEventArgs e) => EndMove();
    private void EndMove()
    {
        if (!_moving) return;
        _moving = false;
        Header.ReleaseMouseCapture();
        CaptureBounds();
        Changed?.Invoke();
    }

    private void Resize_Start(object sender, DragStartedEventArgs e)
    {
        Native.GetCursorPos(out _startMouse);
        Native.GetWindowRect(Handle, out _startRect);
    }

    private void Resize_Delta(object sender, DragDeltaEventArgs e)
    {
        Native.GetCursorPos(out var now);
        int dx = now.X - _startMouse.X, dy = now.Y - _startMouse.Y;
        string edge = (string)((Thumb)sender).Tag;
        int left = _startRect.Left, top = _startRect.Top, right = _startRect.Right, bottom = _startRect.Bottom;
        int minWidth = (int)Math.Ceiling(230 * Scale), minHeight = (int)Math.Ceiling(160 * Scale);
        if (edge.Contains('L')) left = Math.Min(left + dx, right - minWidth);
        if (edge.Contains('R')) right = Math.Max(right + dx, left + minWidth);
        if (!State.RolledUp)
        {
            if (edge.Contains('T')) top = Math.Min(top + dy, bottom - minHeight);
            if (edge.Contains('B')) bottom = Math.Max(bottom + dy, top + minHeight);
        }
        State.X = left; State.Y = top; State.Width = right - left;
        if (!State.RolledUp) State.Height = bottom - top;
        ApplyBounds();
    }

    private void Resize_End(object sender, DragCompletedEventArgs e) { CaptureBounds(); Changed?.Invoke(); }
    private void Roll_Click(object sender, RoutedEventArgs e) => ToggleRoll();
    private void ToggleRoll()
    {
        _hoverCollapse.Stop();
        _hoverExpanded = false;
        State.RolledUp = !State.RolledUp;
        ApplyBounds();
        Changed?.Invoke();
    }

    private void SetHoverExpanded(bool expanded)
    {
        if (!State.RolledUp || _hoverExpanded == expanded || IsClosed || _quickHidden) return;
        _hoverExpanded = expanded;
        ApplyBounds();
    }

    private void Title_Down(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2) return;
        EndMove();
        BeginRename();
        e.Handled = true;
    }

    private void BeginRename()
    {
        TitleEditor.Text = State.Title;
        TitleText.Visibility = Visibility.Collapsed;
        TitleEditor.Visibility = Visibility.Visible;
        Native.SetFocus(ContentHandle);
        TitleEditor.Focus();
        TitleEditor.SelectAll();
    }

    private void FinishRename(bool save)
    {
        if (TitleEditor.Visibility != Visibility.Visible) return;
        if (save && !string.IsNullOrWhiteSpace(TitleEditor.Text)) State.Title = TitleEditor.Text.Trim();
        TitleText.Text = State.Title;
        TitleEditor.Visibility = Visibility.Collapsed;
        TitleText.Visibility = Visibility.Visible;
        Changed?.Invoke();
    }

    private void TitleEditor_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Escape) { FinishRename(e.Key == Key.Enter); e.Handled = true; }
    }
    private void TitleEditor_LostFocus(object sender, KeyboardFocusChangedEventArgs e) => FinishRename(true);

    private void Menu_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        menu.Opened += (_, _) => _menuOpen = true;
        menu.Closed += (_, _) => { _menuOpen = false; if (_hoverExpanded && !IsMouseOver) _hoverCollapse.Start(); };
        void Add(string title, Action action) { var item = new MenuItem { Header = title }; item.Click += (_, _) => action(); menu.Items.Add(item); }
        Add("Переименовать", BeginRename);
        Add(State.RolledUp ? "Развернуть" : "Свернуть в заголовок", ToggleRoll);
        Add("Открыть папку блока", () => OpenPath(FileStorage.Folder(State.Id)));
        menu.Items.Add(new Separator());
        Add("Удалить блок…", () => { if (!_importing) RemoveRequested?.Invoke(this); });
        menu.PlacementTarget = (Button)sender;
        menu.IsOpen = true;
    }

    private void StartWatcher()
    {
        _watcher = new FileSystemWatcher(FileStorage.Folder(State.Id)) { NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite };
        FileSystemEventHandler changed = (_, _) => QueueRefresh();
        _watcher.Created += changed; _watcher.Deleted += changed; _watcher.Changed += changed;
        _watcher.Renamed += (_, _) => QueueRefresh();
        _watcher.Error += (_, _) => QueueRefresh();
        _watcher.EnableRaisingEvents = true;
    }

    private void QueueRefresh()
    {
        if (IsClosed || Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(new Action(() => { if (!IsClosed) { _refreshTimer.Stop(); _refreshTimer.Start(); } }));
    }

    private async Task ReloadAsync()
    {
        if (IsClosed) return;
        if (_loadingItems) { _reloadItems = true; return; }
        _loadingItems = true;
        try
        {
            do
            {
                _reloadItems = false;
                string folder = FileStorage.Folder(State.Id);
                var items = await Task.Run(() => Directory.EnumerateFileSystemEntries(folder)
                    .OrderBy(p => Path.GetFileName(p), StringComparer.CurrentCultureIgnoreCase)
                    .Select(p => new FenceItem { Path = p, Name = Path.GetExtension(p).Equals(".lnk", StringComparison.OrdinalIgnoreCase) ? Path.GetFileNameWithoutExtension(p) : Path.GetFileName(p), Icon = ShellIcons.Get(p) }).ToList());
                if (IsClosed) return;
                string? selected = (ItemsList.SelectedItem as FenceItem)?.Path;
                _items.Clear();
                foreach (var item in items) _items.Add(item);
                ItemsList.SelectedItem = _items.FirstOrDefault(i => i.Path == selected);
                EmptyHint.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                CountText.Text = _importing ? "Перемещение файлов…" : $"Объектов: {items.Count}";
            } while (_reloadItems && !IsClosed);
        }
        catch (Exception ex) { ConfigurationStore.Log(ex); CountText.Text = "Не удалось прочитать папку"; }
        finally { _loadingItems = false; }
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = !_importing && e.Data.GetDataPresent(DataFormats.FileDrop) && (e.AllowedEffects & DragDropEffects.Move) != 0 ? DragDropEffects.Move : DragDropEffects.None;
        DropOverlay.Visibility = e.Effects == DragDropEffects.Move ? Visibility.Visible : Visibility.Collapsed;
        e.Handled = true;
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        e.Handled = true;
        if (_importing || !e.Data.GetDataPresent(DataFormats.FileDrop) || (e.AllowedEffects & DragDropEffects.Move) == 0) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
        // Перенос выполняется нами; источник не должен повторно удалять файлы.
        e.Effects = DragDropEffects.None;
        _importing = true;
        CountText.Text = "Перемещение файлов…";
        try
        {
            var errors = await FileStorage.ImportAsync(State.Id, paths);
            if (errors.Count > 0) MessageBox.Show(string.Join(Environment.NewLine, errors), "Некоторые файлы не перемещены", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex) { Report(ex); }
        finally { _importing = false; await ReloadAsync(); }
    }

    private void Items_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        var container = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (container?.DataContext is FenceItem item) { OpenPath(item.Path); e.Handled = true; }
    }

    private void Items_RightClick(object sender, MouseButtonEventArgs e)
    {
        var container = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (container?.DataContext is not FenceItem item) return;
        ItemsList.SelectedItem = item;
        e.Handled = true;
        try { _menuOpen = true; Native.GetCursorPos(out var p); ShellContextMenu.Show(ContentHandle, item.Path, p.X, p.Y); QueueRefresh(); }
        catch (Exception ex) { Report(ex); }
        finally { _menuOpen = false; if (_hoverExpanded && !IsMouseOver) _hoverCollapse.Start(); }
    }

    internal void SetQuickHidden(bool hidden, bool animate = true)
    {
        _quickHidden = hidden;
        if (hidden) { _hoverCollapse.Stop(); _hoverExpanded = false; }
        if (IsClosed) return;
        if (!hidden) { Show(); ApplyBounds(); Native.ShowWindow(Handle, 4); }
        if (!animate)
        {
            BeginAnimation(DesktopOpacityProperty, null);
            DesktopOpacity = hidden ? 0 : 0.94;
            ApplyNativeOpacity();
            if (hidden) { Native.ShowWindow(Handle, 0); Hide(); }
            return;
        }
        var animation = new DoubleAnimation(DesktopOpacity, hidden ? 0 : 0.94, TimeSpan.FromMilliseconds(160));
        animation.Completed += (_, _) => { if (_quickHidden && !IsClosed) { Native.ShowWindow(Handle, 0); Hide(); } };
        BeginAnimation(DesktopOpacityProperty, animation);
    }

    internal static void OpenPath(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { Report(ex); }
    }

    private static void Report(Exception ex)
    {
        ConfigurationStore.Log(ex);
        MessageBox.Show(ex.Message, "MyFences", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private static T? FindAncestor<T>(DependencyObject? element) where T : DependencyObject
    {
        while (element != null)
        {
            if (element is T found) return found;
            element = element is Visual || element is System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
        }
        return null;
    }
}
