using System.Net;
using System.Net.Http;
using System.Text;

namespace MoghimiVPN;

public sealed class SubscriptionService
{
    private const int MaxResponseBytes = 4 * 1024 * 1024;
    private const int MaxProfiles = 2000;

    private static readonly HttpClient SharedClient = new(
        new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            ConnectTimeout = TimeSpan.FromSeconds(8),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        })
    {
        Timeout = TimeSpan.FromSeconds(25)
    };

    private readonly HttpClient _client;

    public SubscriptionService(HttpClient? client = null) =>
        _client = client ?? SharedClient;

    public async Task<List<ProxyProfile>> FetchAsync(
        SubscriptionItem subscription,
        CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(subscription.Url, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") ||
            !string.IsNullOrWhiteSpace(uri.UserInfo))
            throw new InvalidOperationException("آدرس اشتراک باید یک URL معتبر HTTP/HTTPS بدون نام کاربری یا رمز تعبیه‌شده باشد.");

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("MoghimiVPN-Windows/1.0");
        request.Headers.Accept.ParseAdd("text/plain");
        request.Headers.Accept.ParseAdd("application/octet-stream");

        using var response = await _client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        response.EnsureSuccessStatusCode();

        var finalUri = response.RequestMessage?.RequestUri;
        if (uri.Scheme == Uri.UriSchemeHttps &&
            finalUri?.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("سرور اشتراک تلاش کرد اتصال HTTPS را به HTTP ناامن هدایت کند؛ به‌روزرسانی رد شد.");

        if (response.Content.Headers.ContentLength is long contentLength &&
            contentLength > MaxResponseBytes)
            throw new InvalidOperationException("حجم پاسخ اشتراک بیش از 4 مگابایت است و برای امنیت رد شد.");

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[32 * 1024];
        int read;
        while ((read = await input.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > MaxResponseBytes)
                throw new InvalidOperationException("حجم پاسخ اشتراک از سقف 4 مگابایت بیشتر شد.");
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }

        var body = Encoding.UTF8.GetString(buffer.ToArray()).Trim().Trim('\uFEFF');
        if (string.IsNullOrWhiteSpace(body))
            throw new InvalidOperationException("سرور اشتراک پاسخ خالی برگرداند؛ کانفیگ‌های قبلی حفظ شدند.");

        var profiles = ProfileCodec.DecodeMany(body);
        if (profiles.Count == 0)
            throw new InvalidOperationException("هیچ کانفیگ قابل پشتیبانی در اشتراک پیدا نشد؛ کانفیگ‌های قبلی حفظ شدند.");
        if (profiles.Count > MaxProfiles)
            throw new InvalidOperationException($"اشتراک بیش از {MaxProfiles} کانفیگ دارد و برای جلوگیری از مصرف غیرعادی منابع رد شد.");

        foreach (var profile in profiles)
            profile.SubscriptionId = subscription.Id;
        return profiles;
    }
}
