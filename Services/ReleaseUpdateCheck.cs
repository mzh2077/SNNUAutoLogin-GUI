using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace SNNUAutoLogin.Services;

internal static class ReleaseUpdateCheck
{
    private const string Repository = "mzh2077/SNNUAutoLogin-GUI";
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(5) };

    public static async Task<string?> FindUpdateAsync(Version installed)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://api.github.com/repos/{Repository}/releases?per_page=20");
        request.Headers.UserAgent.ParseAdd("SNNUAutoLogin/" + installed.ToString(3));
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await Client.SendAsync(request).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;
        string json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        return FindNewerTag(json, installed);
    }

    internal static string? FindNewerTag(string json, Version installed)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array) return null;

        Version newest = installed;
        string? newestTag = null;
        foreach (var release in document.RootElement.EnumerateArray())
        {
            if (release.ValueKind != JsonValueKind.Object
                || !release.TryGetProperty("draft", out var draft) || draft.ValueKind != JsonValueKind.False
                || !release.TryGetProperty("prerelease", out var preview) || preview.ValueKind != JsonValueKind.False
                || !release.TryGetProperty("tag_name", out var tagName) || tagName.ValueKind != JsonValueKind.String)
                continue;

            var tag = tagName.GetString();
            if (tag is null) continue;
            var number = tag.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? tag[1..] : tag;
            if (!Version.TryParse(number, out var version) || version.Build < 0) continue;

            var normalized = new Version(version.Major, version.Minor, version.Build, Math.Max(0, version.Revision));
            if (normalized <= newest) continue;

            newest = normalized;
            newestTag = tag;
        }
        return newestTag;
    }

    public static string ReleaseUrl(string tag) =>
        $"https://github.com/{Repository}/releases/tag/{Uri.EscapeDataString(tag)}";
}
