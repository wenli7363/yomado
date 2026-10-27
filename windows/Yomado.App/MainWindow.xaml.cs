using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Yomado.Core;
using Yomado.Windows;

namespace Yomado.App;

public partial class MainWindow : Window
{
    private readonly PanelModel model = new();
    private readonly Desktop? desktop;
    private readonly DriverClient? driver;
    private readonly Workspace? workspace;
    private readonly WeChatRecovery? wechat;
    private readonly ProfileStore? store;
    private readonly List<DisplayProfile> profiles = [];
    private readonly Dictionary<uint, string> profileMessages = [];
    private uint? startingSlot;
    private readonly Dictionary<string, ImageSource?> icons = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly bool preview;
    private readonly bool readOnlyProbe;
    private readonly VirtualDisplayPointerBoundary? pointerBoundary;
    public static readonly uint ActivateMessage = RegisterWindowMessage("Yomado.Windows.ShowPanel");
    private TrayIcon? tray;
    private bool busy, exiting;
    private Point dragStart;
    private Task<UpdateResult>? updateCheck;

    public MainWindow(bool preview = false, bool readOnlyProbe = false, VirtualDisplayPointerBoundary? pointerBoundary = null)
    {
        this.pointerBoundary = pointerBoundary;
        this.readOnlyProbe = readOnlyProbe;
        this.preview = preview; InitializeComponent(); DataContext = model;
        if (!preview) SourceInitialized += (_, _) => HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)!.AddHook(ThemeMessage);
        if (preview) { LoadPreview(); return; }
        desktop = new Desktop(); driver = new DriverClient(); workspace = new Workspace(desktop);
        var stateDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Yomado");
        store = new ProfileStore(Path.Combine(stateDirectory, "screens.json"));
        wechat = new WeChatRecovery(desktop, Path.Combine(stateDirectory, "wechat-recovery.json"));
        workspace.CheckpointBeforeMove = wechat.BeforeMove;
        profiles.AddRange(store.Load());
        Loaded += (_, _) =>
        {
            Action show = readOnlyProbe ? () => { } : ShowPanel;
            Action menu = readOnlyProbe ? () => { } : ShowTrayMenu;
            tray = new TrayIcon(new WindowInteropHelper(this).Handle, show, menu);
            if (!readOnlyProbe) timer.Start();
            try { Refresh(); if (!readOnlyProbe) EnsureOnPhysical(); } catch (Exception ex) { model.Status = ex.Message; }
        };
        timer.Tick += (_, _) => { if (!busy) { try { wechat.Poll(); workspace.RefreshOrigins(); Refresh(); var status = wechat.TakeStatus(); if (status is not null) model.Status = status; } catch (Exception ex) { model.Status = ex.Message; } } };
        Closing += OnClosing;
    }

    private void Refresh()
    {
        if (desktop is null || driver is null) return;
        var displays = desktop.GetDisplays();
        pointerBoundary?.UpdateDisplays(displays);
        var windows = desktop.GetWindows();
        var cards = displays.Select(display =>
        {
            var profile = profiles.SingleOrDefault(p => display.IsYomado && p.Slot == display.Slot);
            var rows = windows.Where(window => GeometryPolicy.Owner(window.Frame, displays)?.Id == display.Id)
                .GroupBy(window => window.ApplicationPath, StringComparer.OrdinalIgnoreCase).Select(group =>
                {
                    if (!icons.TryGetValue(group.Key, out var icon)) icons[group.Key] = icon = ShellIcons.ForApplication(group.Key);
                    return new ApplicationRow(group.Key, group.First().ApplicationName, display.Id, group.Count(), icon);
                }).OrderBy(row => row.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
            var detail = $"{display.Bounds.Width} × {display.Bounds.Height} · {display.Scale:P0}";
            return new ScreenCard(display, profile?.DisplayName ?? display.Name, detail, rows);
        }).ToArray();
        // Keep cards stable while polling so keyboard focus and scroll positions survive inventory changes.
        if (!model.Screens.Select(CardSignature).SequenceEqual(cards.Select(CardSignature)))
        { model.Screens.Clear(); foreach (var card in cards) model.Screens.Add(card); }
        var health = DriverHealth.Inspect();
        var payload = DriverInstaller.HasPayload(AppContext.BaseDirectory);
        var hint = !payload ? "安装文件不完整，请使用完整 Windows 发布包。" : DriverInstaller.NeedsDevelopmentTrust(AppContext.BaseDirectory) ?
            "本地开发版：首次安装需要信任随包测试证书，并允许 Windows 管理员权限。" : "安装需要管理员权限，Windows 会显示权限提示。";
        model.SetDriverHealth(health, payload, hint);
        RefreshSavedProfiles(displays);
        Desktop.KeepAwake(displays.Any(display => display.IsYomado));
        if (displays.Any(display => display.IsYomado) && pointerBoundary is not null &&
            (pointerBoundary.Error ?? pointerBoundary.GetStatus().ClipError) is { } pointerError)
            model.Status = "鼠标边界保护异常：" + pointerError;
    }
    private void RefreshSavedProfiles(IReadOnlyList<DisplayInfo> displays)
    {
        var saved = profiles.Where(profile => !displays.Any(display => display.IsYomado && display.Slot == profile.Slot)).ToArray();
        foreach (var row in model.SavedProfiles.Where(row => saved.All(profile => profile.Slot != row.Profile.Slot)).ToArray())
            model.SavedProfiles.Remove(row);
        for (var index = 0; index < saved.Length; index++)
        {
            var profile = saved[index];
            var row = model.SavedProfiles.SingleOrDefault(row => row.Profile.Slot == profile.Slot);
            if (row is null) { row = new(profile); model.SavedProfiles.Insert(index, row); }
            else if (model.SavedProfiles.IndexOf(row) != index) model.SavedProfiles.Move(model.SavedProfiles.IndexOf(row), index);
            row.Update(profile, model.CanCreateScreens, !busy, startingSlot == profile.Slot, profileMessages.GetValueOrDefault(profile.Slot, ""));
        }
    }
    private static string CardSignature(ScreenCard card) => $"{card.Display}:{card.Title}:{string.Join('|', card.Applications.Select(row => row.Path + ':' + row.WindowCount))}";
    private nint ThemeMessage(nint window, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x7e && pointerBoundary is not null)
        {
            // Drop stale pixel bounds immediately during hotplug/rearrangement.
            pointerBoundary.UpdateDisplays([]);
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (busy || exiting) return;
                try { Refresh(); } catch (Exception ex) { model.Status = ex.Message; }
            }));
        }
        if (message is 0x1a or 0x31a) Dispatcher.BeginInvoke(new Action(() => Palette.Apply()));
        if (message == ActivateMessage && !readOnlyProbe) { ShowPanel(); handled = true; }
        return 0;
    }

    private async Task Operation(Func<Task> action)
    {
        if (busy || preview || readOnlyProbe) return;
        busy = true; model.IsBusy = true; timer.Stop();
        try { RefreshSavedProfiles(model.Screens.Select(card => card.Display).ToArray()); await action(); }
        catch (Exception ex) { model.Status = ex.Message; MessageBox.Show(this, ex.Message, "yomado", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally
        {
            busy = false; model.IsBusy = false;
            if (!exiting)
            {
                try { Refresh(); } catch (Exception ex) { model.Status = ex.Message; }
                finally { timer.Start(); }
            }
        }
    }
    private Task Move(string path, string? source, DisplayInfo target) => Operation(async () =>
    {
        if (target.IsYomado) await Task.Run(() => { wechat!.EnsureMoveAllowed(path); workspace!.MoveApplication(path, source, target.Id); });
        else if (!target.IsVirtual && source is not null) await Task.Run(() =>
        {
            if (WeChatRecovery.IsWeChat(path)) { wechat!.RecoverApplication(path); workspace!.RefreshOrigins(); }
            workspace!.RestoreApplication(path, source, WeChatRecovery.IsWeChat(path) ? null : target.Id);
        });
        else throw new InvalidOperationException("请选择 yomado 虚拟屏或返回实体屏。");
        model.Status = target.IsYomado ? "窗口已移入。原位置在本次运行期间保留。" : "窗口已返回实体屏。";
    });

    private Task StartProfile(DisplayProfile profile) => Operation(async () =>
    {
        startingSlot = profile.Slot; profileMessages.Remove(profile.Slot);
        RefreshSavedProfiles(model.Screens.Select(card => card.Display).ToArray());
        try
        {
        var before = desktop!.GetDisplays();
        if (before.Any(d => d.IsYomado && d.Slot == profile.Slot)) return;
        DisplayTopology.RequireExtended(before);
        model.Status = "正在创建虚拟屏…";
        await driver!.CreateAsync(profile);
        DisplayInfo? added = null;
        for (var retry = 0; retry < 80; retry++)
        {
            added = desktop.GetDisplays().SingleOrDefault(d => d.IsYomado && d.Slot == profile.Slot);
            if (added is not null) break;
            await Task.Delay(100);
        }
        if (added is null) throw new InvalidOperationException("驱动已接收创建请求，但尚未确认屏幕可用。请刷新或关闭此配置后重试。");
        DisplayTopology.VerifyAddition(before, desktop.GetDisplays(), profile, requirePosition: false);
        DisplayLayout.Append(added, before, desktop);
        added = DisplayTopology.VerifyAddition(before, desktop.GetDisplays(), profile);
        pointerBoundary?.UpdateDisplays(desktop.GetDisplays());
        model.Status = profile.HiDpi && Math.Abs(added.Scale - 2) > 0.05 ?
            "虚拟屏已创建，使用 2 倍像素。Windows 缩放可在系统显示设置中调整。" : "虚拟屏已创建，可以移入本次任务的窗口。";
        if (pointerBoundary is { IsActive: false })
            model.Status = "虚拟屏已创建，但鼠标边界保护未启用：" + pointerBoundary.Error;
        }
        catch (Exception ex) { profileMessages[profile.Slot] = ex.Message; throw; }
        finally { startingSlot = null; }
    });

    private async Task<bool> StopProfile(DisplayProfile profile)
    {
        await driver!.EnsureConnectedAsync();
        var before = desktop!.GetDisplays();
        var display = before.SingleOrDefault(d => d.IsYomado && d.Slot == profile.Slot);
        if (display is null)
        {
            if (!driver.GetConnectedSlots().Contains(profile.Slot)) return true;
            throw new InvalidOperationException("驱动中的虚拟屏尚未出现在显示清单，无法核对待恢复窗口，请稍后刷新重试。");
        }
        EnsureOnPhysical();
        model.Status = "正在恢复窗口…";
        await Task.Run(() => { wechat!.RecoverDisplay(display.Id); workspace!.RefreshOrigins(); workspace.RestoreDisplay(display.Id); });
        var offered = await Task.Run(() => workspace!.InspectClose(display.Id));
        var confirmed = false;
        if (offered.Windows.Count > 0)
        {
            ShowPanel();
            confirmed = MessageBox.Show(this,
                $"{profile.DisplayName} 中仍有不能自动移回的对话框或其他窗口：\n\n{offered.Summary}\n\n继续会移除这块虚拟屏，这些窗口的位置由 Windows 处理。可取消后手动处理窗口。是否继续？",
                "关闭前确认", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
            if (!confirmed) { model.Status = "已取消关闭，虚拟屏已保留。"; return false; }
        }
        DisplayTopology.VerifyUnchanged(before, desktop.GetDisplays());
        workspace!.VerifyClose(offered, confirmed);
        driver!.Remove(profile.Slot);
        for (var retry = 0; retry < 40; retry++)
        {
            var after = desktop.GetDisplays();
            if (!after.Any(d => d.IsYomado && d.Slot == profile.Slot) && !driver.GetConnectedSlots().Contains(profile.Slot))
            { DisplayTopology.VerifyRemoval(before, after, display.Id); model.Status = "虚拟屏已关闭，实体屏与其他工作屏保持原排列。"; return true; }
            await Task.Delay(100);
        }
        throw new InvalidOperationException("窗口已恢复，屏幕移除尚未获确认。请刷新后重试。");
    }

    private void AddClicked(object sender, RoutedEventArgs e)
    {
        if (busy || preview || readOnlyProbe) return;
        if (!model.DriverReady) { model.Status = "请先点击一键安装驱动，等待驱动就绪后再创建虚拟屏。"; return; }
        NewProfile();
    }
    private async void StartSavedProfileClicked(object sender, RoutedEventArgs e)
    {
        if (busy || preview || readOnlyProbe || ((FrameworkElement)sender).Tag is not SavedProfileRow row || !row.CanStart) return;
        var profile = profiles.SingleOrDefault(profile => profile.Slot == row.Profile.Slot);
        if (profile is not null) await StartProfile(profile);
    }
    private void EditSavedProfileClicked(object sender, RoutedEventArgs e)
    {
        if (busy || preview || readOnlyProbe || ((FrameworkElement)sender).Tag is not SavedProfileRow row || !row.CanEdit) return;
        var profile = profiles.SingleOrDefault(profile => profile.Slot == row.Profile.Slot);
        if (profile is not null) EditProfile(profile, desktop!.GetDisplays().Any(display => display.IsYomado && display.Slot == profile.Slot));
    }
    private bool ShowProfileDialog(ProfileDialog dialog)
    {
        var previousFocus = Keyboard.FocusedElement;
        dialog.Owner = this; busy = true; model.IsBusy = true; timer.Stop();
        try { return dialog.ShowDialog() == true; }
        finally
        {
            busy = false; model.IsBusy = false;
            try { Refresh(); } catch (Exception ex) { model.Status = ex.Message; }
            if (!exiting) timer.Start();
            // A hidden or minimized owner must not steal focus when the dialog ends.
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                if (IsVisible && WindowState != WindowState.Minimized && IsActive && previousFocus is UIElement { IsVisible: true, IsEnabled: true } element)
                    element.Focus();
            }));
        }
    }
    private async void NewProfile()
    {
        if (busy || preview || readOnlyProbe) return;
        var slot = Enumerable.Range(0, DisplayProfile.MaximumDisplays).FirstOrDefault(i => profiles.All(p => p.Slot != i), -1);
        if (slot < 0) { model.Status = "最多保存 16 块虚拟屏。"; return; }
        var dialog = new ProfileDialog(new((uint)slot, $"工作屏 {slot + 1}"), false, true);
        if (!ShowProfileDialog(dialog)) return;
        try { store!.Save(profiles.Append(dialog.Result!)); profiles.Add(dialog.Result!); Refresh(); await StartProfile(dialog.Result!); }
        catch (Exception ex) { model.Status = ex.Message; }
    }
    private void EditProfile(DisplayProfile profile, bool active)
    {
        if (busy || preview || readOnlyProbe) return;
        var dialog = new ProfileDialog(profile, active, false);
        if (!ShowProfileDialog(dialog)) return;
        try
        {
            var result = dialog.Result!;
            if (desktop!.GetDisplays().Any(display => display.IsYomado && display.Slot == profile.Slot))
                result = profile with { Name = result.Name };
            var updated = profiles.Select(p => p.Slot == profile.Slot ? result : p).ToArray();
            store!.Save(updated); profiles.Clear(); profiles.AddRange(updated); profileMessages.Remove(profile.Slot);
            Refresh(); model.Status = "已保存屏幕配置。";
        }
        catch (Exception ex) { model.Status = ex.Message; }
    }
    private void ScreenMenuClicked(object sender, RoutedEventArgs e)
    {
        if (busy || preview || ((FrameworkElement)sender).Tag is not ScreenCard card || !card.Display.IsYomado) return;
        var profile = profiles.SingleOrDefault(p => p.Slot == card.Display.Slot);
        if (profile is null) { model.Status = "未找到此屏幕的配置，暂时无法管理。"; return; }
        var menu = new ContextMenu(); menu.Items.Add(Menu("重命名…", () => EditProfile(profile, true)));
        menu.Items.Add(new Separator()); menu.Items.Add(Menu("关闭虚拟屏", async () => await Operation(() => StopProfile(profile))));
        OpenMenu(menu, (FrameworkElement)sender);
    }
    private void AppContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (preview || busy || ((FrameworkElement)sender).Tag is not ApplicationRow row) { e.Handled = true; return; }
        var menu = new ContextMenu(); var source = model.Screens.Single(card => card.Display.Id == row.DisplayId).Display;
        if (source.IsYomado) menu.Items.Add(Menu("返回原位置", async () => await Operation(async () =>
        { await RestoreRow(row); model.Status = "窗口已返回实体屏。"; })));
        foreach (var target in model.Screens.Where(card => card.Display.IsYomado && card.Display.Id != row.DisplayId))
            menu.Items.Add(Menu($"移入 {target.Title}", async () => await Move(row.Path, row.DisplayId, target.Display)));
        if (menu.Items.Count == 0) { e.Handled = true; return; }
        ((FrameworkElement)sender).ContextMenu = menu;
    }
    private void AppMouseDown(object sender, MouseButtonEventArgs e) { dragStart = e.GetPosition(this); ((FrameworkElement)sender).Focus(); }
    private void AppMouseMove(object sender, MouseEventArgs e)
    {
        if (busy || preview || e.LeftButton != MouseButtonState.Pressed || ((FrameworkElement)sender).Tag is not ApplicationRow row) return;
        var delta = e.GetPosition(this) - dragStart;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        DragDrop.DoDragDrop((DependencyObject)sender, new DataObject("Yomado.Application", new ApplicationDrag(row.Path, row.DisplayId)), DragDropEffects.Move);
    }
    private bool ValidDrop(DragEventArgs e, ScreenCard target)
    {
        if (preview || busy) return false;
        if (e.Data.GetData("Yomado.Application") is ApplicationDrag drag)
        {
            var source = model.Screens.SingleOrDefault(card => card.Display.Id == drag.SourceDisplayId)?.Display;
            return source is not null && source.Id != target.Display.Id &&
                (target.Display.IsYomado || (source.IsYomado && !target.Display.IsVirtual));
        }
        return target.Display.IsYomado && e.Data.GetData(DataFormats.FileDrop) is string[] { Length: 1 } files &&
            (Path.GetExtension(files[0]).Equals(".exe", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(files[0]).Equals(".lnk", StringComparison.OrdinalIgnoreCase));
    }
    private void ScreenDragOver(object sender, DragEventArgs e)
    {
        var border = (Border)sender; var card = (ScreenCard)border.Tag;
        e.Effects = ValidDrop(e, card) ? DragDropEffects.Move : DragDropEffects.None; e.Handled = true;
        if (e.Effects == DragDropEffects.Move) border.Background = new SolidColorBrush(SystemColors.HighlightColor) { Opacity = 0.1 };
        else border.SetResourceReference(BackgroundProperty, "CardBackground");
    }
    private void ScreenDragLeave(object sender, DragEventArgs e) => ((Border)sender).SetResourceReference(BackgroundProperty, "CardBackground");
    private async void ScreenDropped(object sender, DragEventArgs e)
    {
        var border = (Border)sender; var card = (ScreenCard)border.Tag;
        border.SetResourceReference(BackgroundProperty, "CardBackground"); e.Handled = true;
        if (!ValidDrop(e, card)) return;
        if (e.Data.GetData("Yomado.Application") is ApplicationDrag drag) await Move(drag.ApplicationPath, drag.SourceDisplayId, card.Display);
        else if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            try { await Move(ResolveExecutable(files[0]), null, card.Display); }
            catch (Exception ex) { model.Status = ex.Message; }
        }
    }
    private static string ResolveExecutable(string path)
    {
        if (Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            var type = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("无法读取快捷方式。");
            dynamic shell = Activator.CreateInstance(type)!; dynamic shortcut = shell.CreateShortcut(path);
            try { path = (string)shortcut.TargetPath; }
            finally { Marshal.FinalReleaseComObject(shortcut); Marshal.FinalReleaseComObject(shell); }
        }
        if (!File.Exists(path) || !Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("请拖入已运行应用的 exe 或快捷方式。");
        return Path.GetFullPath(path);
    }
    private static MenuItem Menu(string label, Action action) { var item = new MenuItem { Header = label }; item.Click += (_, _) => action(); return item; }
    private static void OpenMenu(ContextMenu menu, FrameworkElement anchor) { menu.PlacementTarget = anchor; menu.IsOpen = true; }
    private void ShowTrayMenu()
    {
        var menu = new ContextMenu(); menu.Items.Add(Menu("打开控制面板", ShowPanel));
        menu.Items.Add(Menu("添加或启动虚拟屏…", ShowPanel));
        foreach (var card in model.Screens.Where(card => card.Display.IsYomado))
        {
            var screen = new MenuItem { Header = card.Title };
            foreach (var row in card.Applications) screen.Items.Add(Menu($"返回 {row.Name}", async () => await Operation(() => RestoreRow(row))));
            var profile = profiles.SingleOrDefault(p => p.Slot == card.Display.Slot);
            if (profile is not null) { screen.Items.Add(new Separator()); screen.Items.Add(Menu("关闭虚拟屏", async () => await Operation(() => StopProfile(profile)))); }
            menu.Items.Add(screen);
        }
        menu.Items.Add(new Separator()); menu.Items.Add(Menu("退出 yomado", async () => await ExitAsync())); menu.IsOpen = true;
    }
    private void ShowPanel() { if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal; Show(); EnsureOnPhysical(); Activate(); }
    private Task RestoreRow(ApplicationRow row) => Task.Run(() =>
    {
        if (WeChatRecovery.IsWeChat(row.Path)) { wechat!.RecoverApplication(row.Path); workspace!.RefreshOrigins(); }
        workspace!.RestoreApplication(row.Path, row.DisplayId);
    });
    private void EnsureOnPhysical()
    {
        if (desktop is null) return;
        var physical = GeometryPolicy.Physical(desktop.GetDisplays()); if (physical is null) return;
        if (!GetWindowRect(new WindowInteropHelper(this).Handle, out var actual)) return;
        var frame = new PixelRect(actual.Left, actual.Top, actual.Right - actual.Left, actual.Bottom - actual.Top);
        var owner = GeometryPolicy.Owner(frame, desktop.GetDisplays());
        if (owner is { IsVirtual: false } && owner.WorkArea.Contains(frame)) return;
        var destination = owner is { IsVirtual: false } ? owner : physical;
        var target = GeometryPolicy.Place(frame, destination, owner is { IsVirtual: false } ? GeometryPolicy.Relative(frame, owner) : null);
        SetWindowPos(new WindowInteropHelper(this).Handle, 0, target.X, target.Y, target.Width, target.Height, 0x14);
    }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [StructLayout(LayoutKind.Sequential)] private struct WindowRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(nint window, out WindowRect rect);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string message);
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (exiting) return;
        e.Cancel = true;
        if (tray is { IsVisible: true }) Hide(); else await ExitAsync();
    }
    public void WritePanelProbe(string path)
    {
        try
        {
            Refresh();
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new { passed = model.Screens.Count > 0, screens = model.Screens.Count,
                trayVisible = tray?.IsVisible ?? false, driverReady = model.DriverReady, driverButton = model.DriverButtonText,
                canInstallDriver = model.CanInstallDriver, canCreateScreens = model.CanCreateScreens,
                scope = "Own invisible control panel and read-only desktop inventory. No virtual displays or input." }));
        }
        finally { DisposeProbe(); }
    }
    public void DisposeProbe()
    {
        if (!readOnlyProbe) return;
        timer.Stop(); tray?.Dispose(); desktop?.Dispose(); driver?.Dispose();
    }
    private void LicensesClicked(object sender, RoutedEventArgs e)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "THIRD-PARTY-NOTICES.md");
        if (File.Exists(path)) App.OpenLink(path);
        else MessageBox.Show(this, "应用与托管库采用 MIT 许可证。显示驱动的微软示例衍生代码采用 MS-PL。完整许可见源码及发布包。", "开源许可");
    }
    private Task ExitAsync() => Operation(async () =>
    {
        foreach (var slot in driver!.GetConnectedSlots())
        {
            var profile = profiles.SingleOrDefault(p => p.Slot == slot) ?? throw new InvalidOperationException("此屏幕缺少配置，请先手动恢复窗口。");
            if (!await StopProfile(profile)) return;
        }
        exiting = true; timer.Stop(); tray?.Dispose(); desktop!.Dispose(); Desktop.KeepAwake(false); driver.Dispose(); Application.Current.Shutdown();
    });
    private async void InstallDriverClicked(object sender, RoutedEventArgs e)
    {
        if (preview || readOnlyProbe || busy || !model.CanInstallDriver) return;
        var root = AppContext.BaseDirectory;
        var needsTrust = DriverInstaller.NeedsDevelopmentTrust(root);
        try { DriverInstaller.ValidatePackage(root, needsTrust); }
        catch (Exception ex) { model.Status = ex.Message; MessageBox.Show(this, ex.Message, "无法安装驱动", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        if (needsTrust && MessageBox.Show(this,
            "这是本地开发版。安装会将随包测试证书加入本机的受信任根和受信任发布者，然后安装显示驱动。\n\n证书：Yomado Local Development\n指纹：" + DriverInstaller.DevelopmentThumbprint + "\n\n是否继续？",
            "安装本地开发驱动", MessageBoxButton.YesNo, MessageBoxImage.Information) != MessageBoxResult.Yes) return;
        await Operation(async () =>
        {
            if (desktop!.GetDisplays().Any(display => display.IsYomado) || DriverHealth.Inspect().ActiveScreens != 0)
                throw new InvalidOperationException("请先关闭所有 Yomado 虚拟屏，再安装驱动。");
            model.DriverInstalling = true; model.DriverStatus = "正在安装虚拟显示驱动…";
            model.Status = "请在 Windows 权限提示中选择“是”。安装完成后会自动刷新。";
            driver!.Dispose();
            try
            {
                await Dispatcher.Yield(DispatcherPriority.Background);
                var result = await DriverInstaller.LaunchAsync(root, needsTrust);
                if (result == DriverInstallResult.Success && !DriverHealth.Inspect().IsReady) result = DriverInstallResult.NotReady;
                model.Status = DriverInstaller.Message(result);
                if (result is not (DriverInstallResult.Success or DriverInstallResult.Cancelled))
                    MessageBox.Show(this, model.Status, "驱动安装", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            finally { model.DriverInstalling = false; }
        });
    }
    private void GitHubClicked(object sender, RoutedEventArgs e) => App.OpenLink("https://github.com/wenli7363/yomado");
    private async void CheckUpdatesClicked(object sender, RoutedEventArgs e)
    {
        if (preview) return;
        try
        {
            model.Status = "正在检查 Windows 版本…";
            updateCheck ??= UpdateChecker.CheckAsync("0.1.0"); var result = await updateCheck;
            model.Status = result.IsNewer ? $"发现 Windows 新版本 {result.Version}。" : "当前已是最新 Windows 版本。";
            if (result.IsNewer && MessageBox.Show(this, $"发现 {result.Version}，打开发布页面？", "检查更新", MessageBoxButton.YesNo) == MessageBoxResult.Yes) App.OpenLink(result.ReleaseUrl);
        }
        catch (Exception ex) { model.Status = ex.Message; }
        finally { updateCheck = null; }
    }
    private void LoadPreview()
    {
        var physical = new DisplayInfo("preview-main", "", "主显示器", new(0, 0, 1920, 1080), new(0, 0, 1920, 1040), true, false);
        var codex = new DisplayInfo("preview-codex", "", "", new(1920, 0, 1920, 1080), new(1920, 0, 1920, 1040), false, true, 0);
        var browser = new DisplayInfo("preview-browser", "", "", new(3840, 0, 2560, 1440), new(3840, 0, 2560, 1400), false, true, 1);
        model.Screens.Add(new(physical, "主显示器", "1920 × 1080 · 100%", [new("", "Visual Studio", physical.Id, 1, null), new("", "终端", physical.Id, 2, null)]));
        model.Screens.Add(new(codex, "虚拟屏 1 · Codex", "1920 × 1080 · 100%", [new("", "QDoc", codex.Id, 1, null)]));
        model.Screens.Add(new(browser, "虚拟屏 2 · 工作屏", "2560 × 1440 · 100%", []));
        model.SetDriverHealth(new(DriverCondition.Ready, "虚拟显示驱动已就绪 · 示例显示数据"), true);
        var saved = new SavedProfileRow(new(2, "Blender", 2560, 1440, true));
        saved.Update(saved.Profile, true, true, false, ""); model.SavedProfiles.Add(saved);
        model.Status = "拖动应用图标移入虚拟屏，完成后返回原位置。";
    }
}
