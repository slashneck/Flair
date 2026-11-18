using System.Net;
using System.Text;
using System.Text.Json;
using Flair.Core;

namespace Flair.Platform;

public sealed record GalleryItem(string Id, string Title, string? By, int Downloads, int Count, string CreatedAt, Preset Preview);

public sealed record GalleryPage(string Q, string Sort, int Page, int Pages, int Total, List<GalleryItem> Items);

/// <summary>
/// The presets people share on Patchbay. Read only and without an account: Flair asks for the list only while the
/// Presets view is open, and everything that comes back goes through the same checks as a file you open yourself.
/// </summary>
public static class PresetGallery
{
    public const string Site = "https://patchbay-sage.vercel.app";
    public const string SharePage = Site + "/app/flair/presets";
#if DEBUG
    // lets a test point Flair at a local copy of the API
    private static readonly string Api = Environment.GetEnvironmentVariable("FLAIR_GALLERY_URL") is { Length: > 0 } local ? local.TrimEnd('/') : Site;
#else
    private const string Api = Site;
#endif
    private const int PerPage = 24;
    private const int MaxListBytes = 2 * 1024 * 1024;
    private const int MaxFileBytes = 512 * 1024;

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"Flair/{Updater.Current}");
        return http;
    }

    public static async Task<GalleryPage> ListAsync(string? q, string? sort, int page)
    {
        q = new string((q ?? "").Where(c => char.IsLetterOrDigit(c) || c == ' ').Take(60).ToArray()).Trim();
        sort = sort == "popular" ? "popular" : "new";
        page = Math.Clamp(page, 1, 500);
        string url = $"{Api}/api/presets?app=flair&q={Uri.EscapeDataString(q)}&sort={sort}&page={page}&per={PerPage}";

        using var doc = JsonDocument.Parse(await GetAsync(url, MaxListBytes));
        var root = doc.RootElement;
        var items = new List<GalleryItem>();
        if (root.TryGetProperty("items", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var it in list.EnumerateArray().Take(50))
            {
                if (Text(it, "id") is not { } id || !Guid.TryParse(id, out _)) continue;
                if (!it.TryGetProperty("preview", out var raw) || raw.ValueKind != JsonValueKind.Object) continue;
                Preset? preview;
                try { preview = raw.Deserialize<Preset>(PresetStore.Json); }
                catch (JsonException) { continue; }
                if (preview == null) continue;
                preview.Hotkey = new HotkeySetting();
                preview.Sanitize();
                preview.DropBadButtons();
                string title = (Text(it, "title") ?? preview.Name).Trim();
                if (title.Length == 0) title = preview.Name;
                items.Add(new GalleryItem(
                    id,
                    title.Length > 60 ? title[..60] : title,
                    Text(it, "by") is { } by && by.Length <= 39 ? by : null,
                    Math.Max(0, Number(it, "downloads")),
                    Math.Clamp(Number(it, "count"), 1, 50),
                    Text(it, "createdAt") ?? "",
                    preview));
            }
        }
        return new GalleryPage(q, sort, page, Math.Clamp(Number(root, "pages"), 1, 500), Math.Max(0, Number(root, "total")), items);
    }

    /// <summary>Downloads one shared file as text. It still has to pass <see cref="PresetFile"/>'s checks to get in.</summary>
    public static Task<string> DownloadAsync(string id)
    {
        if (!Guid.TryParse(id, out var guid)) throw new InvalidDataException("That preset is gone");
        return GetAsync($"{Api}/api/presets/{guid:D}/download", MaxFileBytes);
    }

    private static async Task<string> GetAsync(string url, int maxBytes)
    {
        HttpResponseMessage response;
        try
        {
            response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Log.Warn($"Patchbay didn't answer: {ex.Message}");
            throw new InvalidOperationException("Can't reach Patchbay right now. Check your internet and try again.");
        }
        using (response)
        {
            if (response.Content.Headers.ContentLength > maxBytes) throw new InvalidDataException("Patchbay sent more than a preset list should be");
            await using var stream = await response.Content.ReadAsStreamAsync();
            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            int read;
            while ((read = await stream.ReadAsync(chunk)) > 0)
            {
                if (buffer.Length + read > maxBytes) throw new InvalidDataException("Patchbay sent more than a preset list should be");
                buffer.Write(chunk, 0, read);
            }
            string body = Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
            if (response.IsSuccessStatusCode) return body;
            if (response.StatusCode == HttpStatusCode.NotFound) throw new InvalidOperationException("That preset is gone");
            throw new InvalidOperationException("Patchbay is having trouble right now. Try again in a minute.");
        }
    }

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int Number(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n) ? n : 0;
}
