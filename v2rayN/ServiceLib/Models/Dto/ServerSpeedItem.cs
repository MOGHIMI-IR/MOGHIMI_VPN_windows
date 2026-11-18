namespace ServiceLib.Models.Dto;

[Serializable]
public class ServerSpeedItem : ServerStatItem
{
    public long ProxyUp { get; set; }

    public long ProxyDown { get; set; }

    // Exact byte deltas used by the server-authoritative Moghimi quota meter.
    public long ProxyUpBytes { get; set; }
    public long ProxyDownBytes { get; set; }

    public long DirectUp { get; set; }

    public long DirectDown { get; set; }
}

[Serializable]
public class TrafficItem
{
    public ulong Up { get; set; }

    public ulong Down { get; set; }
}
