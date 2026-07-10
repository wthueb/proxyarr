namespace Proxyarr.Clients;

/// <summary>
/// Describes how one download client type is proxied. To support a new download client, implement
/// this interface and register it in <see cref="DownloadClientEndpoints.AddDownloadClients"/>; to
/// cover a new endpoint of an existing client, add a <see cref="ProxyRoute"/> to its adapter.
/// Routes are pure pass-throughs by default; the optional <see cref="ProxyRoute"/> hooks
/// (<c>Validate</c>, <c>OnRequest</c>, <c>TransformRequest</c>, <c>TransformResponse</c>,
/// <c>Handle</c>) let a route inspect, rewrite, or answer requests itself. Adapters are DI
/// singletons, so hooks that need services take them via constructor injection.
/// </summary>
public interface IDownloadClientAdapter
{
    /// <summary>Value matched against the <c>type</c> field of a client in the YAML config.</summary>
    string Type { get; }

    /// <summary>The endpoints exposed for this client type; everything else is rejected.</summary>
    IReadOnlyList<ProxyRoute> Routes { get; }
}
