using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using Yomado.Core;
using Yomado.Windows;

namespace Yomado.App;

public sealed class PanelModel : INotifyPropertyChanged
{
    public ObservableCollection<ScreenCard> Screens { get; } = [];
    public ObservableCollection<SavedProfileRow> SavedProfiles { get; } = [];
    public bool HasSavedProfiles => SavedProfiles.Count != 0;
    public PanelModel() => SavedProfiles.CollectionChanged += (_, _) => Changed(nameof(HasSavedProfiles));
    private string status = "拖动应用图标，将窗口移入虚拟屏。";
    private string driverStatus = "正在读取屏幕…";
    public string Status { get => status; set { status = value; Changed(); } }
    public string DriverStatus { get => driverStatus; set { driverStatus = value; Changed(); } }
    private DriverHealth health = new(DriverCondition.Starting, "正在检测驱动…");
    private bool installing, busy, hasInstaller;
    public bool DriverReady => health.IsReady;
    public bool CanCreateScreens => DriverReady && !busy;
    public string DriverButtonText => installing ? "正在安装…" : "一键安装驱动";
    public bool CanInstallDriver => !busy && !DriverReady && health.Condition != DriverCondition.Duplicate && hasInstaller && health.ActiveScreens == 0;
    public string DriverHint { get; private set; } = "";
    public bool DriverInstalling { get => installing; set { installing = value; NotifyDriver(); } }
    public bool IsBusy { get => busy; set { busy = value; NotifyDriver(); } }
    public void SetDriverHealth(DriverHealth value, bool installerAvailable, string hint = "")
    { health = value; hasInstaller = installerAvailable; DriverHint = value.IsReady ? "" : hint; DriverStatus = value.Message; NotifyDriver(); }
    private void NotifyDriver()
    {
        Changed(nameof(DriverReady)); Changed(nameof(CanCreateScreens)); Changed(nameof(DriverButtonText));
        Changed(nameof(CanInstallDriver)); Changed(nameof(DriverInstalling)); Changed(nameof(DriverHint));
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
public sealed record ApplicationRow(string Path, string Name, string DisplayId, int WindowCount, ImageSource? Icon)
{
    public string Tooltip => $"{Name} · {WindowCount} 个窗口";
}
public sealed record ScreenCard(DisplayInfo Display, string Title, string Detail, IReadOnlyList<ApplicationRow> Applications)
{
    public string Kind => Display.IsYomado ? "虚拟屏" : Display.IsVirtual ? "其他虚拟屏" : Display.IsPrimary ? "实体屏 · 主屏" : "实体屏";
    public bool CanManage => Display.IsYomado;
    public bool Empty => Applications.Count == 0;
    public double CardWidth => Math.Clamp(Display.Bounds.Width / Display.Scale * 0.16, 255, 430);
    public double CardHeight
    {
        get
        {
            // Reserve complete rows before adding a scrollbar. Small landscape
            // cards must not crop the second row just to mimic a monitor ratio.
            var columns = Math.Max(1, (int)((CardWidth - 32 - 16) / 60));
            var rows = Math.Clamp((int)Math.Ceiling(Applications.Count / (double)columns), 2, 4);
            return Math.Max(112 + rows * 60, CardWidth * Display.Bounds.Height / Display.Bounds.Width);
        }
    }
    public Brush FrameBrush => Display.IsYomado ? System.Windows.SystemColors.HighlightBrush : System.Windows.SystemColors.ControlDarkBrush;
}
public sealed record ApplicationDrag(string ApplicationPath, string SourceDisplayId);

public sealed class SavedProfileRow : INotifyPropertyChanged
{
    public DisplayProfile Profile { get; private set; }
    public string Name => Profile.DisplayName;
    public string Detail => $"{Profile.Width} × {Profile.Height}{(Profile.HiDpi ? " · HiDPI · 2 倍像素" : "")}";
    public string Message { get; private set; } = "";
    public bool CanStart { get; private set; }
    public bool CanEdit { get; private set; }
    public string StartText { get; private set; } = "启动";
    public SavedProfileRow(DisplayProfile profile) => Profile = profile;
    public void Update(DisplayProfile profile, bool canStart, bool canEdit, bool starting, string message)
    {
        var startText = starting ? "启动中…" : "启动";
        if (Profile == profile && CanStart == canStart && CanEdit == canEdit && StartText == startText && Message == message) return;
        Profile = profile; CanStart = canStart; CanEdit = canEdit; StartText = startText; Message = message;
        PropertyChanged?.Invoke(this, new(null));
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}
