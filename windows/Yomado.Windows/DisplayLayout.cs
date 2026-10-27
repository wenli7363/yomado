using System.ComponentModel;
using Yomado.Core;

namespace Yomado.Windows;

public static class DisplayLayout
{
    public static void Append(DisplayInfo target, IReadOnlyList<DisplayInfo> before, IDesktop desktop)
    {
        if (!target.IsYomado || before.Any(d => d.Id == target.Id) || GeometryPolicy.Physical(before) is null)
            throw new InvalidOperationException("新虚拟屏或实体屏状态无效。");
        var config = Desktop.DisplayConfiguration();
        for (var index = 0; index < config.Paths.Length; index++)
        {
            var path = config.Paths[index];
            var name = Desktop.SourceName(path.Source).Name;
            if (path.Source.ModeIndex >= config.Modes.Length) throw new InvalidOperationException("显示配置缺少来源模式。");
            var original = before.SingleOrDefault(d => string.Equals(d.DeviceName, name, StringComparison.OrdinalIgnoreCase));
            if (string.Equals(name, target.DeviceName, StringComparison.OrdinalIgnoreCase))
                config.Modes[path.Source.ModeIndex].Position = new() { X = GeometryPolicy.RightEdge(before), Y = 0 };
            else if (original is not null)
                config.Modes[path.Source.ModeIndex].Position = new() { X = original.Bounds.X, Y = original.Bounds.Y };
        }
        // Validate the exact supplied configuration before applying it; never permit Windows to rearrange existing screens.
        var error = Native.SetDisplayConfig((uint)config.Paths.Length, config.Paths, (uint)config.Modes.Length, config.Modes, 0x20 | 0x40);
        if (error != 0) throw new Win32Exception(error, "Windows 未接受虚拟屏排列。");
        error = Native.SetDisplayConfig((uint)config.Paths.Length, config.Paths, (uint)config.Modes.Length, config.Modes, 0x20 | 0x80);
        if (error != 0) throw new Win32Exception(error, "无法应用虚拟屏排列。");
        IReadOnlyList<DisplayInfo> after = [];
        for (var attempt = 0; attempt < 60; attempt++)
        {
            after = desktop.GetDisplays();
            if (before.All(original => after.Any(actual => actual.Id == original.Id && actual.Bounds.Near(original.Bounds) && actual.IsPrimary == original.IsPrimary))) break;
            Thread.Sleep(25);
        }
        foreach (var original in before)
        {
            var actual = after.SingleOrDefault(d => d.Id == original.Id);
            if (actual is null || !actual.Bounds.Near(original.Bounds) || actual.IsPrimary != original.IsPrimary)
                throw new InvalidOperationException($"屏幕“{original.Name}”的位置或分辨率未获确认，请检查系统显示设置；新虚拟屏已保留供恢复。");
        }
        var added = after.SingleOrDefault(d => d.Id == target.Id);
        if (added is null || added.Bounds.X != GeometryPolicy.RightEdge(before))
            throw new InvalidOperationException("尚未确认虚拟屏的位置，请刷新显示清单。");
    }
}
