using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace MoghimiVPN;

public sealed class CloudSync
{
    private const string ApiUrl = "https://hxfnoaeyjnkecegpppcy.supabase.co/functions/v1/windows-api";
    private const int MaxResponseBytes = 4 * 1024 * 1024;
    private const int MaxAggregateBytes = 16 * 1024 * 1024;
    private const int MaxEntries = 2000;
    private const int MaxProfiles = 2000;

    private static readonly HttpClient SharedClient = new(
        new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(6),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
        })
    {
        Timeout = TimeSpan.FromSeconds(18)
    };

    private readonly HttpClient _client;

    public CloudSync(HttpClient? client = null) =>
        _client = client ?? SharedClient;

    private sealed class ResponsePayload
    {
        public bool ok { get; set; }
        public string? code { get; set; }
        public string? message { get; set; }
        public List<Entry>? entries { get; set; }
    }

    private sealed class Entry
    {
        public string? title { get; set; }
        public string? entry_type { get; set; }
        public string? config_text { get; set; }
    }

    public async Task<List<ProxyProfile>> FetchActiveProfilesAsync(
        string deviceToken,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(deviceToken) || deviceToken.Length != 64)
            throw new InvalidOperationException("ابتدا کد فعال‌سازی ویندوز را در بخش اشتراک ثبت کن.");

        using var request = new HttpRequestMessage(HttpMethod.Post, ApiUrl)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { action = "profiles" }),
                Encoding.UTF8,
                "application/json")
        };
        request.Headers.TryAddWithoutValidation("x-moghimi-device-token", deviceToken);
        request.Headers.UserAgent.ParseAdd("MoghimiVPN-Windows/1.0");

        using var response = await _client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        EnsureProjectResponse(response);
        var json = await ReadBoundedTextAsync(response.Content, MaxResponseBytes, cancellationToken);

        ResponsePayload payload;
        try
        {
            payload = JsonSerializer.Deserialize<ResponsePayload>(
                json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("پاسخ فهرست سرورهای ویندوز قابل خواندن نیست.");
        }

        if (!response.IsSuccessStatusCode || !payload.ok)
            throw new InvalidOperationException(payload.message ??
                (payload.code == "subscription_inactive"
                    ? "اشتراک ویندوز فعال نیست یا حجم/مدت آن تمام شده است."
                    : "دریافت کانفیگ‌های ویندوز ناموفق بود."));

        var entries = payload.entries ?? [];
        if (entries.Count > MaxEntries)
            throw new InvalidOperationException($"فهرست ابری بیش از {MaxEntries} ورودی دارد و برای جلوگیری از مصرف غیرعادی منابع رد شد.");

        var imported = new List<ProxyProfile>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        long aggregatePayloadBytes = 0;

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.Equals(entry.entry_type, "text", StringComparison.OrdinalIgnoreCase))
                continue;

            var text = entry.config_text ?? string.Empty;
            aggregatePayloadBytes += Encoding.UTF8.GetByteCount(text);
            if (aggregatePayloadBytes > MaxAggregateBytes)
                throw new InvalidOperationException("حجم کل کانفیگ‌های ویندوز از سقف ۱۶ مگابایت بیشتر شد؛ کانفیگ‌های قبلی حفظ می‌شوند.");

            foreach (var profile in ProfileCodec.DecodeMany(text))
            {
                if (profile.Name == "سرور جدید" && !string.IsNullOrWhiteSpace(entry.title))
                    profile.Name = entry.title!;
                profile.SubscriptionId = "moghimi-cloud";
                var key = $"{profile.Protocol}|{profile.Server}|{profile.Port}|{profile.Uuid}|{profile.Password}";
                if (seen.Add(key))
                    imported.Add(profile);

                if (imported.Count > MaxProfiles)
                    throw new InvalidOperationException($"فهرست ابری بیش از {MaxProfiles} کانفیگ فعال دارد و برای امنیت رد شد.");
            }
        }

        return imported;
    }

    private static void EnsureProjectResponse(HttpResponseMessage response)
    {
        var expected = new Uri(ApiUrl);
        var finalUri = response.RequestMessage?.RequestUri;
        if (finalUri is null ||
            finalUri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(finalUri.Host, expected.Host, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("پاسخ سرویس ویندوز از میزبان دیگری هدایت شد و برای امنیت رد شد.");
    }

    private static async Task<string> ReadBoundedTextAsync(
        HttpContent content,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is long length && length > maxBytes)
            throw new InvalidOperationException("حجم پاسخ ابری از سقف مجاز بیشتر است.");

        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var chunk = new byte[32 * 1024];
        int read;

        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (output.Length + read > maxBytes)
                throw new InvalidOperationException("حجم پاسخ ابری هنگام دریافت از سقف مجاز عبور کرد.");
            await output.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }

        return Encoding.UTF8.GetString(output.ToArray()).Trim().Trim('\uFEFF');
    }
}
