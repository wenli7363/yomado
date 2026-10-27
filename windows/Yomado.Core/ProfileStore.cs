using System.Text.Json;

namespace Yomado.Core;

public sealed class ProfileStore(string path)
{
    public IReadOnlyList<DisplayProfile> Load()
    {
        if (!File.Exists(path)) return [new(0, "Codex")];
        try
        {
            var profiles = JsonSerializer.Deserialize<List<DisplayProfile>>(File.ReadAllText(path)) ?? [];
            return profiles.Where(p => p is not null && p.IsValid).DistinctBy(p => p.Slot).Select(p => p with { Name = p.Name.Trim() }).ToArray();
        }
        catch (JsonException ex) { throw new InvalidDataException("屏幕配置损坏，原文件已保留。", ex); }
    }

    public void Save(IEnumerable<DisplayProfile> profiles)
    {
        var values = profiles.ToArray();
        if (values.Any(p => !p.IsValid) || values.Select(p => p.Slot).Distinct().Count() != values.Length)
            throw new ArgumentException("屏幕名称、分辨率或编号无效。");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(values, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, true);
    }
}
