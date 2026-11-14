using System.Net.Http;

namespace MoghimiVPN;

public sealed class SubscriptionService
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(20) };

    public async Task<List<ProxyProfile>> FetchAsync(SubscriptionItem subscription, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(subscription.Url, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
            throw new InvalidOperationException("آدرس اشتراک باید با http:// یا https:// شروع شود.");

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("MoghimiVPN-Windows/1.0");
        using var response = await Client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var profiles = ProfileCodec.DecodeMany(body);
        if (profiles.Count == 0)
            throw new InvalidOperationException("در آدرس واردشده کانفیگ قابل پشتیبانی پیدا نشد.");
        foreach (var p in profiles) p.SubscriptionId = subscription.Id;
        return profiles;
    }
}
