namespace MoghimiVPN;

public sealed class CloudSync
{
    private readonly WindowsLicenseService _license;

    public CloudSync(WindowsLicenseService license) => _license = license;

    public Task<List<ProxyProfile>> FetchActiveProfilesAsync(CancellationToken cancellationToken = default) =>
        _license.FetchMoghimiCloudProfilesAsync(cancellationToken);
}
