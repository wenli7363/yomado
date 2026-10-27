namespace Yomado.Core;

// Compare observations rather than writing an old configuration back over a
// monitor hotplug or a user's concurrent display-settings change.
public static class DisplayTopology
{
    public static void RequireExtended(IReadOnlyList<DisplayInfo> displays)
    {
        if (displays.Count == 0 || displays.Any(d => d.IsMirrored || !d.Bounds.IsUsable ||
            !double.IsFinite(d.Scale) || d.Scale <= 0) ||
            displays.Select(d => d.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != displays.Count ||
            displays.Count(d => d.IsPrimary) != 1 || displays.Any(d => d.IsPrimary && d.IsVirtual) ||
            GeometryPolicy.Physical(displays) is null)
            throw new InvalidOperationException("需要保留实体主屏并使用扩展显示模式；请检查 Windows 显示设置后重试。");
        for (var i = 0; i < displays.Count; i++)
            for (var j = i + 1; j < displays.Count; j++)
                if (displays[i].Bounds.Intersection(displays[j].Bounds) != 0)
                    throw new InvalidOperationException("检测到重叠或复制显示，请切换为扩展显示后重试。");
    }

    public static bool SameDisplay(DisplayInfo expected, DisplayInfo actual) =>
        string.Equals(expected.Id, actual.Id, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(expected.DeviceName, actual.DeviceName, StringComparison.OrdinalIgnoreCase) &&
        expected.Bounds == actual.Bounds && expected.WorkArea == actual.WorkArea &&
        expected.IsPrimary == actual.IsPrimary && expected.IsYomado == actual.IsYomado &&
        expected.IsExternalVirtual == actual.IsExternalVirtual && expected.Slot == actual.Slot &&
        Math.Abs(expected.Scale - actual.Scale) < 0.001 && expected.IsMirrored == actual.IsMirrored &&
        expected.Rotation == actual.Rotation && expected.OutputScaling == actual.OutputScaling &&
        expected.ModeSignature == actual.ModeSignature &&
        (expected.RefreshDenominator == 0 || actual.RefreshDenominator == 0 ?
            expected.RefreshNumerator == actual.RefreshNumerator && expected.RefreshDenominator == actual.RefreshDenominator :
            (ulong)expected.RefreshNumerator * actual.RefreshDenominator ==
                (ulong)actual.RefreshNumerator * expected.RefreshDenominator);

    public static void VerifyUnchanged(IReadOnlyList<DisplayInfo> expected, IReadOnlyList<DisplayInfo> actual)
    {
        if (expected.Count != actual.Count || expected.Any(d =>
                actual.Count(a => string.Equals(a.Id, d.Id, StringComparison.OrdinalIgnoreCase) && SameDisplay(d, a)) != 1))
            throw new InvalidOperationException("显示排列、缩放或模式已变化，已停止操作；请刷新并检查系统显示设置。");
    }

    public static DisplayInfo VerifyAddition(IReadOnlyList<DisplayInfo> before, IReadOnlyList<DisplayInfo> after,
        DisplayProfile profile, bool requirePosition = true)
    {
        RequireExtended(before);
        var added = after.Where(d => d.IsYomado && d.Slot == profile.Slot && before.All(b => b.Id != d.Id)).ToArray();
        if (added.Length != 1 || after.Count != before.Count + 1)
            throw new InvalidOperationException("未能确认唯一新增虚拟屏，已保留当前显示状态供检查。");
        VerifyUnchanged(before, after.Where(d => d.Id != added[0].Id).ToArray());
        var target = added[0];
        if (target.IsMirrored || target.IsPrimary || target.Bounds.Width != profile.PixelWidth ||
            target.Bounds.Height != profile.PixelHeight || (requirePosition &&
            (target.Bounds.X != GeometryPolicy.RightEdge(before) || target.Bounds.Y != 0)))
            throw new InvalidOperationException("虚拟屏的扩展模式、分辨率或位置未获确认，已保留屏幕供恢复。");
        if (requirePosition) RequireExtended(after);
        return target;
    }

    public static void VerifyRemoval(IReadOnlyList<DisplayInfo> before, IReadOnlyList<DisplayInfo> after, string removedId) =>
        VerifyUnchanged(before.Where(d => d.Id != removedId).ToArray(), after);
}
