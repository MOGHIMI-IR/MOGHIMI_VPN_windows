using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace MoghimiVPN;

public sealed class CloudSync
{
    private const string ProjectUrl = "https://atrwhvyhopifugphfcpe.supabase.co";
    private const string PublishableKey = "sb_publishable_9jauFOO0mFzTSs5rtI2Orw_ZekPGGdt";
    private const string StorageBucket = "v2ray-files";
    private const int MaxResponseBytes = 4 * 1024 * 1024;
    private const int MaxEntries = 2000;
    private const int MaxProfiles = 2000;
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(18) };

    private sealed class Entry
    {
        public string? title { get; set; }
        public string? entry_type { get; set; }
        public string? config_text { get; set; }
        public string? file_path { get; set; }
    }

    public async Task<List<ProxyProfile>> FetchActiveProfilesAsync(CancellationToken cancellationToken = default)
    {
        var endpoint = $"{ProjectUrl}/rest/v1/v2ray_entries?select=id,title,entry_type,config_text,file_path,file_name&is_active=eq.true&order=created_at.desc";
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.TryAddWithoutValidation("apikey", PublishableKey);
        request.Headers.UserAgent.ParseAdd("MoghimiVPN-Windows/1.0");
        using var response = await Client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var json = await ReadBoundedTextAsync(response.Content, MaxResponseBytes, cancellationToken);
        var entries = JsonSerializer.Deserialize<List<Entry>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
        if (entries.Count > MaxEntries)
            throw new InvalidOperationException($"فهرست ابری بیش از {MaxEntries} ورودی دارد و برای جلوگیری از مصرف غیرعادی منابع رد شد.");
        var imported = new List<ProxyProfile>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string payload = entry.entry_type switch
            {
                "text" => entry.config_text ?? "",
                "file" when !string.IsNullOrWhiteSpace(entry.file_path) => await DownloadPrivateFileAsync(entry.file_path!, cancellationToken),
                _ => ""
            };
            foreach (var profile in ProfileCodec.DecodeMany(payload))
            {
                if (profile.Name == "سرور جدید" && !string.IsNullOrWhiteSpace(entry.title))
                    profile.Name = entry.title!;
                profile.SubscriptionId = "moghimi-cloud";
                var key = $"{profile.Protocol}|{profile.Server}|{profile.Port}|{profile.Uuid}|{profile.Password}";
                if (seen.Add(key)) imported.Add(profile);
                if (imported.Count > MaxProfiles)
                    throw new InvalidOperationException($"فهرست ابری بیش از {MaxProfiles} کانفیگ فعال دارد و برای امنیت رد شد.");
            }
        }

        // An empty active catalogue is a valid state: the UI will clear the old cloud group.
        return imported;
    }


    private static async Task<string> ReadBoundedTextAsync(
        HttpContent content,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is long length && length > maxBytes)
            throw new InvalidOperationException("حجم داده ابری از سقف مجاز 4 مگابایت بیشتر است.");

        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var chunk = new byte[32 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (output.Length + read > maxBytes)
                throw new InvalidOperationException("حجم داده ابری هنگام دریافت از سقف مجاز 4 مگابایت عبور کرد.");
            await output.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }
        return Encoding.UTF8.GetString(output.ToArray()).Trim().Trim('\uFEFF');
    }

    private static async Task<string> DownloadPrivateFileAsync(string path, CancellationToken cancellationToken)
    {
        var segments = string.Join("/", path.Split('/').Select(Uri.EscapeDataString));
        var signUrl = $"{ProjectUrl}/storage/v1/object/sign/{StorageBucket}/{segments}";
        using var signRequest = new HttpRequestMessage(HttpMethod.Post, signUrl);
        signRequest.Headers.TryAddWithoutValidation("apikey", PublishableKey);
        signRequest.Headers.UserAgent.ParseAdd("MoghimiVPN-Windows/1.0");
        signRequest.Content = new StringContent("{\"expiresIn\":600}", System.Text.Encoding.UTF8, "application/json");
        using var signResponse = await Client.SendAsync(signRequest, cancellationToken);
        signResponse.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await signResponse.Content.ReadAsStringAsync(cancellationToken));
        if (!doc.RootElement.TryGetProperty("signedURL", out var signed) || string.IsNullOrWhiteSpace(signed.GetString()))
            throw new InvalidOperationException("ساخت لینک موقت دریافت فایل کانفیگ ناموفق بود.");
        var signedPath = signed.GetString()!;
        var target = Uri.TryCreate(signedPath, UriKind.Absolute, out var absolute)
            ? absolute
            : new Uri(new Uri(ProjectUrl + "/"), signedPath.TrimStart('/').StartsWith("storage/v1/", StringComparison.Ordinal)
                ? signedPath.TrimStart('/')
                : "storage/v1/" + signedPath.TrimStart('/'));
        var projectUri = new Uri(ProjectUrl + "/");
        if (target.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(target.Host, projectUri.Host, StringComparison.OrdinalIgnoreCase) ||
            !target.AbsolutePath.StartsWith("/storage/v1/object/", StringComparison.Ordinal))
            throw new InvalidOperationException("لینک موقت فایل خارج از دامنه یا مسیر رسمی Storage پروژه است و رد شد.");

        // Signed URLs already authorize the download; intentionally do not attach the API key.
        using var fileResponse = await Client.GetAsync(target, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        fileResponse.EnsureSuccessStatusCode();
        var finalUri = fileResponse.RequestMessage?.RequestUri;
        if (finalUri is null ||
            finalUri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(finalUri.Host, projectUri.Host, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("دانلود فایل ابری به میزبان غیرمجاز هدایت شد و رد شد.");
        return await ReadBoundedTextAsync(fileResponse.Content, MaxResponseBytes, cancellationToken);
    }
}
