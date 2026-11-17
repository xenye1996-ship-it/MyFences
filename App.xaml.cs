using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using MyFences.Interop;
using MyFences.Models;
using MyFences.Services;
using MyFences.Views;
using Forms = System.Windows.Forms;

namespace MyFences;

public partial class App : Application
{
    private readonly List<FenceWindow> _windows = new();
    private readonly ConfigurationStore _store = new();
    private Configuration _configuration = new();
    private Forms.NotifyIcon? _tray;
    private Icon? _trayIcon;
    private DesktopMouseHook? _mouseHook;
    private Mutex? _mutex;
    private bool _ownsMutex, _quitting, _hidden, _smoke;
    private DispatcherTimer? _saveTimer, _desktopTimer;
    private bool _busy;
    private DateTime _lastDesktopError = DateTime.MinValue;
    private SelectionPreviewWindow? _selectionPreview;
    private bool _visualTest;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        if (e.Args.Contains("--self-test"))
        {
            int result = await Diagnostics.SelfTests.RunAsync();
            Shutdown(result);
            return;
        }
        _smoke = e.Args.Contains("--smoke-test");
        _visualTest = e.Args.Contains("--desktop-visual-test");
        try
        {
            if (_smoke)
            {
                ConfigurationStore.UseTestRoot(Path.Combine(Path.GetTempPath(), "MyFences-Smoke-" + Guid.NewGuid().ToString("N")));
            }
            else
            {
                _mutex = new Mutex(true, "Local\\MyFences.Desktop.v1", out _ownsMutex);
                if (!_ownsMutex)
                {
                    MessageBox.Show("MyFences уже работает. Меню находится в системном трее.", "MyFences");
                    Shutdown();
                    return;
                }
            }
            DispatcherUnhandledException += (_, args) =>
            {
                ConfigurationStore.Log(args.Exception);
                args.Handled = true;
                if (!_smoke) MessageBox.Show(args.Exception.Message, "MyFences", MessageBoxButton.OK, MessageBoxImage.Error);
            };
            bool firstRun = !File.Exists(Path.Combine(ConfigurationStore.Root, "config.json"));
            _configuration = _store.Load();
            if (firstRun)
            {
                var area = Forms.Screen.PrimaryScreen!.WorkingArea;
                _configuration.Fences.Add(new FenceState { Title = "Мои приложения", X = area.Left + 70, Y = area.Top + 90 });
                _configuration.Fences.Add(new FenceState { Title = "Документы и проекты", X = area.Left + 480, Y = area.Top + 90, Height = 420 });
            }
            foreach (var state in _configuration.Fences) KeepOnScreen(state);
            CreateTray();
            _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); Save(); };
            _desktopTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _desktopTimer.Tick += (_, _) => ReconcileDesktop();
            ReconcileDesktop();
            _desktopTimer.Start();
            try
            {
                _mouseHook = new DesktopMouseHook(Dispatcher, _smoke);
                _mouseHook.DoubleClicked += ToggleAll;
                _mouseHook.SelectionFinished += ShowSelectionChoice;
            }
            catch (Exception ex) { ConfigurationStore.Log(ex); Balloon("Quick-Hide недоступен. Показать и скрыть блоки можно через трей."); }
            Save();
            if (_store.RecoveryNotice != null) Balloon(_store.RecoveryNotice);
            else if (!_smoke) Balloon("Перетащите файлы в блок. Двойной клик по пустому рабочему столу — скрыть всё. Настройки — в этом меню.");
            if (_smoke) await RunSmokeAsync();
        }
        catch (Exception ex)
        {
            ConfigurationStore.Log(ex);
            if (_smoke) WriteSmokeResult("FAIL: " + ex);
            else MessageBox.Show(ex.Message, "MyFences — запуск не выполнен", MessageBoxButton.OK, MessageBoxImage.Error);
            Quit(save: false, exitCode: 1);
        }
    }

    private void CreateTray()
    {
        string? executable = Environment.ProcessPath;
        _trayIcon = executable != null ? Icon.ExtractAssociatedIcon(executable) : null;
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Создать новый блок", null, (_, _) => Dispatcher.Invoke(CreateFence));
        menu.Items.Add("Показать / скрыть все", null, (_, _) => Dispatcher.Invoke(ToggleAll));
        menu.Items.Add("Показать все блоки", null, (_, _) => Dispatcher.Invoke(() => SetHidden(false)));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Открыть хранилище", null, (_, _) => FenceWindow.OpenPath(FileStorage.StorageRoot));
        menu.Items.Add("Краткая справка", null, (_, _) => ShowHelp());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Выход", null, (_, _) => Dispatcher.Invoke(() => Quit()));
        _tray = new Forms.NotifyIcon { Text = "MyFences · блоки рабочего стола", Icon = _trayIcon ?? SystemIcons.Application, ContextMenuStrip = menu, Visible = true };
        _tray.DoubleClick += (_, _) => Dispatcher.Invoke(() => SetHidden(false));
    }

    private void CreateFence()
    {
        Native.GetCursorPos(out var point);
        var state = new FenceState { X = point.X - 190, Y = point.Y - 100 };
        KeepOnScreen(state);
        _configuration.Fences.Add(state);
        SetHidden(false);
        ReconcileDesktop();
        Save();
    }

    private void ShowSelectionChoice(Rectangle rectangle)
    {
        if (_quitting) return;
        _selectionPreview?.Close();
        var preview = new SelectionPreviewWindow(rectangle);
        _selectionPreview = preview;
        preview.CreateRequested += area =>
        {
            var state = new FenceState { X = area.X, Y = area.Y, Width = Math.Max(230, area.Width), Height = Math.Max(160, area.Height) };
            _configuration.Fences.Add(state);
            SetHidden(false);
            ReconcileDesktop();
            Save();
        };
        preview.Closed += (_, _) => { if (ReferenceEquals(_selectionPreview, preview)) _selectionPreview = null; };
        preview.Show();
    }

    private void ReconcileDesktop()
    {
        if (_quitting || _busy) return;
        _busy = true;
        try
        {
            nint host = DesktopHost.Find();
            _windows.RemoveAll(w => w.IsClosed);
            foreach (var state in _configuration.Fences)
            {
                var window = _windows.FirstOrDefault(w => w.State.Id == state.Id);
                if (window != null && !Native.IsWindow(window.Handle))
                {
                    window.Close();
                    _windows.Remove(window);
                    window = null;
                }
                if (window == null)
                {
                    window = new FenceWindow(state);
                    window.Changed += ScheduleSave;
                    window.RemoveRequested += RemoveFence;
                    _windows.Add(window);
                    try { window.Show(); window.SetQuickHidden(_hidden, false); }
                    catch { _windows.Remove(window); window.Close(); throw; }
                }
                else if (window.Host != host || Native.GetParent(window.Handle) != host)
                    window.Reattach();
                window.KeepWithinScreen();
            }
        }
        catch (Exception ex)
        {
            if (DateTime.UtcNow - _lastDesktopError > TimeSpan.FromMinutes(1))
            {
                ConfigurationStore.Log(ex);
                _lastDesktopError = DateTime.UtcNow;
                Balloon("Ожидание рабочего стола Explorer. MyFences повторит подключение автоматически.");
            }
        }
        finally { _busy = false; }
    }

    private void RemoveFence(FenceWindow window)
    {
        string folder = FileStorage.Folder(window.State.Id);
        string text = "Удалить блок «" + window.State.Title + "»?\n\nФайлы останутся в хранилище. Его папка откроется в Проводнике.";
        if (MessageBox.Show(text, "Удаление блока", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _configuration.Fences.Remove(window.State);
        _windows.Remove(window);
        window.Close();
        Save();
        FenceWindow.OpenPath(folder);
    }

    private void ToggleAll() => SetHidden(!_hidden);
    private void SetHidden(bool hidden)
    {
        _hidden = hidden;
        foreach (var window in _windows.ToArray()) window.SetQuickHidden(hidden);
    }

    private static void KeepOnScreen(FenceState state)
    {
        var rectangle = new Rectangle(state.X, state.Y, Math.Max(state.Width, 220), 42);
        bool visible = Forms.Screen.AllScreens.Any(s =>
        {
            var intersection = Rectangle.Intersect(s.WorkingArea, rectangle);
            return intersection.Width >= 100 && intersection.Height >= 30;
        });
        if (visible) return;
        var area = Forms.Screen.PrimaryScreen!.WorkingArea;
        state.X = area.Left + 40; state.Y = area.Top + 60;
        state.Width = Math.Min(state.Width, Math.Max(230, area.Width - 80));
        state.Height = Math.Min(state.Height, Math.Max(160, area.Height - 100));
    }

    private void ScheduleSave()
    {
        if (_quitting || _saveTimer == null) return;
        _saveTimer.Stop(); _saveTimer.Start();
    }

    private void Save()
    {
        try { _store.Save(_configuration); }
        catch (Exception ex) { ConfigurationStore.Log(ex); Balloon("Не удалось сохранить настройки: " + ex.Message); }
    }

    private void Balloon(string text) => _tray?.ShowBalloonTip(5000, "MyFences", text, Forms.ToolTipIcon.Info);

    private static void ShowHelp() => MessageBox.Show(
        "Перетащите файлы или папки из Проводника в блок: они физически переместятся в хранилище MyFences.\n\n" +
        "• Потяните за шапку — перемещение блока.\n• Потяните за край — изменение размера.\n" +
        "• Двойной клик по названию — переименование.\n• Двойной клик по свободной части шапки или кнопка ⌃ — сворачивание.\n" +
        "• Двойной клик по файлу — открыть.\n• Правая кнопка по файлу — меню Windows.\n" +
        "• Двойной клик по пустому рабочему столу — скрыть / показать блоки.\n\n" +
        "Меню ⋯ открывает папку блока. Файлы можно вернуть на рабочий стол обычным вырезанием и вставкой в Проводнике.\n" +
        "Удаление блока не удаляет его файлы. Настройки сохраняются автоматически.\n\n" + ConfigurationStore.Root,
        "MyFences · справка", MessageBoxButton.OK, MessageBoxImage.Information);

    private async Task RunSmokeAsync()
    {
        await Task.Delay(1800);
        if (_windows.Count != 2 || _windows.Any(w => w.IsClosed || !Native.IsWindow(w.Handle))) throw new InvalidOperationException("Окна не созданы.");
        foreach (var window in _windows)
        {
            if (Native.GetParent(window.Handle) != window.Host) throw new InvalidOperationException("Неверный parent HWND.");
            long style = Native.GetStyle(window.Handle, Native.GwlStyle).ToInt64();
            long extended = Native.GetStyle(window.Handle, Native.GwlExStyle).ToInt64();
            if ((style & Native.WsChild) != 0 || (extended & Native.WsExToolWindow) == 0) throw new InvalidOperationException("Неверные стили HWND.");
            window.ExerciseRollForSmoke();
        }
        string source = Path.Combine(ConfigurationStore.Root, "Проверка.txt");
        File.WriteAllText(source, "MyFences smoke test");
        var errors = await FileStorage.ImportAsync(_windows[0].State.Id, new[] { source });
        if (errors.Count != 0) throw new InvalidOperationException(string.Join("; ", errors));
        await Task.Delay(1300);
        if (_windows[0].DisplayedItemCount != 1) throw new InvalidOperationException("FileSystemWatcher не обновил сетку.");
        SetHidden(true);
        await Task.Delay(350);
        if (_windows.Any(w => Native.IsWindowVisible(w.Handle))) throw new InvalidOperationException("Quick-Hide не скрыл HWND.");
        SetHidden(false);
        await Task.Delay(350);
        if (_windows.Any(w => !Native.IsWindowVisible(w.Handle))) throw new InvalidOperationException("Quick-Hide не показал HWND.");
        foreach (var window in _windows) window.CaptureBounds();
        Save();
        if (_store.Load().Fences.Count != 2) throw new InvalidOperationException("Конфигурация не сохранена.");
        var visual = _windows[0];
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(visual);
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int)Math.Ceiling(visual.ActualWidth * dpi.DpiScaleX),
            (int)Math.Ceiling(visual.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using (var screenshot = File.Create(Path.Combine(AppContext.BaseDirectory, "smoke-window.png"))) encoder.Save(screenshot);
        // Native popup runs its own message loop. Cancel only this test menu.
        string itemPath = Path.Combine(FileStorage.Folder(visual.State.Id), "Проверка.txt");
        Native.GetWindowRect(visual.Handle, out var rect);
        var cancelMenu = Task.Run(async () => { await Task.Delay(900); Native.PostMessage(visual.ContentHandle, 0x1F, 0, 0); });
        ShellContextMenu.Show(visual.ContentHandle, itemPath, rect.Left + 40, rect.Top + 70, smokeCheck: true);
        await cancelMenu;
        var selectionArea = new Rectangle(1100, 600, 340, 260);
        ShowSelectionChoice(selectionArea);
        await Task.Delay(250);
        int oldCount = _configuration.Fences.Count;
        _selectionPreview!.ConfirmForSmoke();
        await Task.Delay(300);
        var added = _configuration.Fences.Last();
        if (_configuration.Fences.Count != oldCount + 1 || added.X != selectionArea.X || added.Y != selectionArea.Y || added.Width != selectionArea.Width || added.Height != selectionArea.Height)
            throw new InvalidOperationException($"Выделение и блок не совпадают: {added.X},{added.Y},{added.Width},{added.Height}.");
        string winD = _visualTest ? await CheckWinDAsync(visual) : "Desktop switching skipped: no Win+D sent.";
        WriteSmokeResult("PASS: desktop ownership, HWND styles, roll-up, file import, watcher, Quick-Hide, JSON roundtrip, native shell menu, selection menu and exact rectangle size.\n" + winD + "\nData: " + ConfigurationStore.Root);
        Quit();
    }

    private async Task<string> CheckWinDAsync(FenceWindow window)
    {
        nint foreground = Native.GetForegroundWindow();
        if (foreground == 0 || DesktopHost.IsSurface(foreground)) return "SKIP Win+D: desktop was already active.";
        bool sent = false;
        try
        {
            SendWinD();
            sent = true;
            await Task.Delay(700);
            Native.GetWindowRect(window.Handle, out var rect);
            var point = new Native.Point { X = rect.Left + 20, Y = rect.Top + 20 };
            nint hit = Native.WindowFromPoint(point);
            // Verify presented desktop pixels, not just HWND flags or an
            // off-screen WPF RenderTargetBitmap.
            using var capture = new Bitmap(rect.Right - rect.Left, rect.Bottom - rect.Top);
            using (var graphics = Graphics.FromImage(capture))
                graphics.CopyFromScreen(rect.Left, rect.Top, 0, 0, capture.Size);
            capture.Save(Path.Combine(AppContext.BaseDirectory, "smoke-desktop-actual.png"), System.Drawing.Imaging.ImageFormat.Png);
            if (!Native.IsWindowVisible(window.Handle) || (hit != window.Handle && !Native.IsChild(window.Handle, hit)))
                throw new InvalidOperationException($"Win+D: контейнер не принимает мышь. Visible={Native.IsWindowVisible(window.Handle)}, Hit={hit} {Native.Class(hit)}, Surface={window.Handle}, Content={window.ContentHandle}.");
            int matches = 0;
            int headerBottom = (int)Math.Round(42 * window.DpiScaleForDiagnostics);
            for (int x = capture.Width / 2; x < capture.Width - 40; x += 9)
            {
                var header = capture.GetPixel(x, Math.Max(10, headerBottom / 2));
                var body = capture.GetPixel(x, Math.Min(capture.Height - 40, headerBottom + 90));
                if (header.R > body.R + 15 && header.G > body.G + 15 && header.B > body.B + 15 &&
                    header.R < 115 && header.G < 125 && body.B < 100) matches++;
            }
            if (matches < 5) throw new InvalidOperationException("HWND существует, но пиксели контейнера не появились на реальном рабочем столе.");
            await CheckSelectionGestureAsync();
            return "PASS Win+D: real desktop pixels, HWND visibility and hit testing.\nPASS: right-drag desktop selection creates a fence through the real mouse hook.";
        }
        finally
        {
            if (sent) { SendWinD(); await Task.Delay(300); }
        }
    }

    private async Task CheckSelectionGestureAsync()
    {
        var screen = Forms.Screen.PrimaryScreen!.WorkingArea;
        var start = new Native.Point { X = screen.Right - 600, Y = screen.Bottom - 500 };
        var end = new Native.Point { X = start.X + 340, Y = start.Y + 260 };
        bool empty = await Task.Run(() => DesktopMouseHook.IsEmpty(start, Native.WindowFromPoint(start)) && DesktopHost.IsSurface(Native.WindowFromPoint(end)));
        if (!empty) throw new InvalidOperationException("Нет пустой области для проверки жеста выделения.");
        Native.GetCursorPos(out var original);
        try
        {
            Native.SetCursorPos(start.X, start.Y);
            SendMouse(0x8);
            await Task.Delay(300);
            Native.SetCursorPos(end.X, end.Y);
            await Task.Delay(160);
            SendMouse(0x10);
            await Task.Delay(500);
            if (_selectionPreview == null) throw new InvalidOperationException("Не появилось меню создания блока после выделения.");
            int count = _configuration.Fences.Count;
            _selectionPreview.ConfirmForSmoke();
            await Task.Delay(300);
            if (_configuration.Fences.Count != count + 1) throw new InvalidOperationException("Выделение не создало блок.");
            var created = _configuration.Fences.Last();
            if (created.X != start.X || created.Y != start.Y || created.Width != 340 || created.Height != 260)
                throw new InvalidOperationException("Созданный блок не совпадает с выделенной областью.");
        }
        finally { SendMouse(0x10); Native.SetCursorPos(original.X, original.Y); }
    }

    private static void SendMouse(uint flags)
    {
        var input = new[] { new Native.Input { Type = 0, Data = new Native.InputUnion { Mouse = new Native.MouseInput { Flags = flags } } } };
        if (Native.SendInput(1, input, System.Runtime.InteropServices.Marshal.SizeOf<Native.Input>()) != 1)
            throw new InvalidOperationException("Не удалось отправить тестовое событие мыши.");
    }

    private static void SendWinD()
    {
        Native.Input Key(ushort key, bool up) => new()
        {
            Type = 1,
            Data = new Native.InputUnion { Keyboard = new Native.KeyboardInput { Key = key, Flags = up ? 2u : 0u } }
        };
        var input = new[] { Key(0x5B, false), Key(0x44, false), Key(0x44, true), Key(0x5B, true) };
        if (Native.SendInput((uint)input.Length, input, System.Runtime.InteropServices.Marshal.SizeOf<Native.Input>()) != input.Length)
            throw new InvalidOperationException("Windows не приняла тестовую комбинацию Win+D.");
    }

    private static void WriteSmokeResult(string text) => File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "smoke-results.txt"), text);

    private void Quit(bool save = true, int exitCode = 0)
    {
        if (_quitting) return;
        if (_windows.Any(w => w.IsImporting)) { Balloon("Дождитесь окончания перемещения файлов перед выходом."); return; }
        _quitting = true;
        _desktopTimer?.Stop(); _saveTimer?.Stop();
        _selectionPreview?.Close();
        if (save) { foreach (var w in _windows.ToArray()) w.CaptureBounds(); Save(); }
        foreach (var w in _windows.ToArray()) w.Close();
        Shutdown(exitCode);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _mouseHook?.Dispose();
        if (_tray != null) { _tray.Visible = false; _tray.ContextMenuStrip?.Dispose(); _tray.Dispose(); }
        _trayIcon?.Dispose();
        if (_ownsMutex) _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
