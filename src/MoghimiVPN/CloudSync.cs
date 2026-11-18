namespace MoghimiVPN;

public interface IWindowsCloudCatalog
{
    Task<List<ProxyProfile>> FetchMoghimiCloudProfilesAsync(CancellationToken cancellationToken = default);
}

public sealed class CloudSync
{
    private readonly IWindowsCloudCatalog _catalog;

    public CloudSync(IWindowsCloudCatalog catalog) => _catalog = catalog;

    public Task<List<ProxyProfile>> FetchActiveProfilesAsync(CancellationToken cancellationToken = default) =>
        _catalog.FetchMoghimiCloudProfilesAsync(cancellationToken);
}
