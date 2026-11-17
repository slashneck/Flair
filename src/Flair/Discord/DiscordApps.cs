using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Flair.Core;

namespace Flair.Discord;

public sealed record AppInfo(string Id, string? Name, string? IconUrl, Dictionary<string, string> Assets, bool Found);

/// <summary>
/// Looks up a Discord application's public name and uploaded pictures, so the preview can say "Playing Your App" and
/// show assets by their key. Both are public pages of the app itself; nothing about you is sent, no sign-in involved.
/// </summary>
public static class DiscordApps
{
    private static readonly ConcurrentDictionary<string, (DateTime At, AppInfo Info)> Cache = new();
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"Flair/{Platform.Updater.Current}");
        return http;
    }

    public static async Task<AppInfo> LookupAsync(string appId)
    {
        appId = new string(appId.Where(char.IsAsciiDigit).ToArray());
        if (appId.Length < 15) return new AppInfo(appId, null, null, new(), false);
        if (Cache.TryGetValue(appId, out var hit) && DateTime.UtcNow - hit.At < TimeSpan.FromMinutes(5)) return hit.Info;

        string? name = null, iconUrl = null;
        bool found = false;
        var assets = new Dictionary<string, string>();
        try
        {
            using var response = await Http.GetAsync($"https://discord.com/api/v10/applications/{appId}/rpc");
            if (response.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                found = true;
                name = doc.RootElement.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (doc.RootElement.TryGetProperty("icon", out var icon) && icon.GetString() is { Length: > 0 } hash)
                    iconUrl = $"https://cdn.discordapp.com/app-icons/{appId}/{hash}.png?size=128";
            }
            else if (response.StatusCode is not (HttpStatusCode.NotFound or HttpStatusCode.BadRequest))
            {
                throw new HttpRequestException($"Discord answered {(int)response.StatusCode}");
            }

            if (found)
            {
                using var list = await Http.GetAsync($"https://discord.com/api/v10/oauth2/applications/{appId}/assets");
                if (list.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
                    foreach (var a in doc.RootElement.EnumerateArray())
                    {
                        string? key = a.TryGetProperty("name", out var k) ? k.GetString() : null;
                        string? id = a.TryGetProperty("id", out var i) ? i.GetString() : null;
                        if (key != null && id != null) assets[key.ToLowerInvariant()] = $"https://cdn.discordapp.com/app-assets/{appId}/{id}.png";
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Looking up application {appId} failed: {ex.Message}");
            return new AppInfo(appId, null, null, assets, true); // offline: assume it's fine, Discord will say otherwise
        }

        var info = new AppInfo(appId, name, iconUrl, assets, found);
        Cache[appId] = (DateTime.UtcNow, info);
        return info;
    }
}
