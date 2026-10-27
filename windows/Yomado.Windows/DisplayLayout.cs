using System.ComponentModel;
using Yomado.Core;

namespace Yomado.Windows;

public static class DisplayLayout
{
    public static void Append(DisplayInfo target, IReadOnlyList<DisplayInfo> before, IDesktop desktop)
    {
        if (!target.IsYomado || before.Any(d => d.Id == target.Id) || GeometryPolicy.Physical(before) is null)
            throw new InvalidOperationException("新虚拟屏或实体屏状态无效。");
        DisplayTopology.RequireExtended(before);
        var observed = desktop.GetDisplays();
        if (target.IsMirrored || observed.Count != before.Count + 1)
            throw new InvalidOperationException("虚拟屏尚未进入独立扩展模式，请检查 Windows 显示设置。");
        DisplayTopology.VerifyUnchanged(before, observed.Where(d => d.Id != target.Id).ToArray());
        var config = Desktop.DisplayConfiguration();
        var matchedTarget = false;
        for (var index = 0; index < config.Paths.Length; index++)
        {
            var path = config.Paths[index];
            var name = Desktop.SourceName(path.Source).Name;
            if (path.Source.ModeIndex >= config.Modes.Length) throw new InvalidOperationException("显示配置缺少来源模式。");
            if (string.Equals(name, target.DeviceName, StringComparison.OrdinalIgnoreCase))
            {
                if (matchedTarget) throw new InvalidOperationException("虚拟屏处于复制显示模式，已停止排列。");
                matchedTarget = true;
                config.Modes[path.Source.ModeIndex].Position = new() { X = GeometryPolicy.RightEdge(before), Y = 0 };
            }
        }
        if (!matchedTarget) throw new InvalidOperationException("显示配置已变化，未找到新增虚拟屏。");
        DisplayTopology.VerifyUnchanged(observed, desktop.GetDisplays());
        // Validate the exact supplied configuration before applying it; never permit Windows to rearrange existing screens.
        var error = Native.SetDisplayConfig((uint)config.Paths.Length, config.Paths, (uint)config.Modes.Length, config.Modes, 0x20 | 0x40);
        if (error != 0) throw new Win32Exception(error, "Windows 未接受虚拟屏排列。");
        error = Native.SetDisplayConfig((uint)config.Paths.Length, config.Paths, (uint)config.Modes.Length, config.Modes, 0x20 | 0x80);
        if (error != 0) throw new Win32Exception(error, "无法应用虚拟屏排列。");
        IReadOnlyList<DisplayInfo> after = [];
        for (var attempt = 0; attempt < 60; attempt++)
        {
            after = desktop.GetDisplays();
            if (before.All(original => after.Any(actual => DisplayTopology.SameDisplay(original, actual)))) break;
            Thread.Sleep(25);
        }
        foreach (var original in before)
        {
            var actual = after.SingleOrDefault(d => d.Id == original.Id);
            if (actual is null || !DisplayTopology.SameDisplay(original, actual))
                throw new InvalidOperationException($"屏幕“{original.Name}”的位置或分辨率未获确认，请检查系统显示设置；新虚拟屏已保留供恢复。");
        }
        var added = after.SingleOrDefault(d => d.Id == target.Id);
        if (added is null || added.IsMirrored || added.IsPrimary || added.Bounds.X != GeometryPolicy.RightEdge(before) || added.Bounds.Y != 0)
            throw new InvalidOperationException("尚未确认虚拟屏的位置，请刷新显示清单。");
        DisplayTopology.RequireExtended(after);
    }
}
