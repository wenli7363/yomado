using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Yomado.Core;

public sealed class SemanticVersion : IComparable<SemanticVersion>
{
    private static readonly Regex Pattern = new(@"^v?(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$", RegexOptions.NonBacktracking);
    private readonly string[] core, prerelease;
    public string Value { get; }
    public SemanticVersion(string value)
    {
        var match = value.Length <= 256 ? Pattern.Match(value) : Match.Empty;
        if (!match.Success) throw new FormatException("版本号格式无效。");
        Value = value.TrimStart('v'); core = [match.Groups[1].Value, match.Groups[2].Value, match.Groups[3].Value];
        prerelease = match.Groups[4].Success ? match.Groups[4].Value.Split('.') : [];
        if (prerelease.Any(p => Digits(p) && p.Length > 1 && p.StartsWith('0'))) throw new FormatException("预发布版本号格式无效。");
    }
    private static bool Digits(string value) => value.All(char.IsAsciiDigit);
    private static int Number(string a, string b) => a.Length != b.Length ? a.Length.CompareTo(b.Length) : string.CompareOrdinal(a, b);
    public int CompareTo(SemanticVersion? other)
    {
        if (other is null) return 1;
        for (var i = 0; i < 3; i++) { var comparison = Number(core[i], other.core[i]); if (comparison != 0) return comparison; }
        if (prerelease.Length == 0 || other.prerelease.Length == 0)
            return prerelease.Length == other.prerelease.Length ? 0 : prerelease.Length == 0 ? 1 : -1;
        for (var i = 0; i < Math.Min(prerelease.Length, other.prerelease.Length); i++)
        {
            var a = prerelease[i]; var b = other.prerelease[i]; var numericA = Digits(a); var numericB = Digits(b);
            var comparison = numericA && numericB ? Number(a, b) : numericA != numericB ? numericA ? -1 : 1 : string.CompareOrdinal(a, b);
            if (comparison != 0) return comparison;
        }
        return prerelease.Length.CompareTo(other.prerelease.Length);
    }
}

public sealed record UpdateResult(bool IsNewer, string Version, string ReleaseUrl);

public static class UpdateChecker
{
    public const string Repository = "wenli7363/yomado";
    public static UpdateResult Evaluate(IEnumerable<JsonElement> releases, string currentVersion)
    {
        var current = new SemanticVersion(currentVersion); SemanticVersion? latest = null; string? tag = null;
        foreach (var release in releases)
        {
            if (release.ValueKind != JsonValueKind.Object || !release.TryGetProperty("draft", out var draft) || draft.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new InvalidDataException("发布列表格式不完整，暂时无法判断更新。");
            if (draft.GetBoolean()) continue;
            if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("发布列表缺少下载包信息。");
            // macOS-only upstream releases must never be offered as a Windows update.
            var windows = assets.EnumerateArray().Any(asset => asset.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String &&
                name.GetString()!.StartsWith("Yomado-Windows-", StringComparison.OrdinalIgnoreCase) && name.GetString()!.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
            if (!windows) continue;
            if (!release.TryGetProperty("tag_name", out var version) || version.ValueKind != JsonValueKind.String) throw new InvalidDataException("发布版本号缺失。");
            var candidate = new SemanticVersion(version.GetString()!);
            if (latest is null || candidate.CompareTo(latest) > 0) { latest = candidate; tag = version.GetString(); }
        }
        if (latest is null) throw new InvalidOperationException("尚无可比较的 Windows 发布版本，请稍后重试或查看 GitHub。");
        return new(latest.CompareTo(current) > 0, latest.Value, $"https://github.com/{Repository}/releases/tag/{Uri.EscapeDataString(tag!)}");
    }

    public static async Task<UpdateResult> CheckAsync(string currentVersion)
    {
        _ = new SemanticVersion(currentVersion);
        using var handler = new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false, UseDefaultCredentials = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("yomado-windows-update-check/0.1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        var releases = new List<JsonElement>();
        for (var page = 1; page <= 10; page++)
        {
            using var response = await client.GetAsync($"https://api.github.com/repos/{Repository}/releases?per_page=100&page={page}");
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests) throw new HttpRequestException("GitHub 暂时限制了查询，请稍后重试。");
            if (response.StatusCode != HttpStatusCode.OK) throw new HttpRequestException("暂时无法获取 GitHub 发布列表，请稍后重试。");
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (json.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("发布列表格式异常。");
            releases.AddRange(json.RootElement.EnumerateArray().Select(item => item.Clone()));
            var more = response.Headers.TryGetValues("Link", out var links) && links.Any(link => link.Contains("rel=\"next\"", StringComparison.Ordinal));
            if (!more) return Evaluate(releases, currentVersion);
        }
        throw new InvalidDataException("无法完整读取发布列表，请前往 GitHub 查看。");
    }
}
