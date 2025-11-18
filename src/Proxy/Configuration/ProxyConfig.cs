using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace Proxy.Configuration;

public enum ProxyRouteKind
{
    Http,
    Grpc
}

public sealed class RouteConfig
{
    public RouteConfig(string pathPrefix, string clusterId, ProxyRouteKind kind, bool stripPrefix = false)
    {
        if (string.IsNullOrWhiteSpace(pathPrefix))
        {
            throw new ArgumentException("Path prefix is required.", nameof(pathPrefix));
        }

        if (!pathPrefix.StartsWith('/'))
        {
            pathPrefix = "/" + pathPrefix;
        }

        if (pathPrefix.Length > 1 && pathPrefix.EndsWith('/'))
        {
            pathPrefix = pathPrefix.TrimEnd('/');
        }

        PathPrefix = new PathString(pathPrefix);
        ClusterId = clusterId ?? throw new ArgumentNullException(nameof(clusterId));
        Kind = kind;
        StripPrefix = stripPrefix;
    }

    public PathString PathPrefix { get; }

    public string ClusterId { get; }

    public ProxyRouteKind Kind { get; }

    public bool StripPrefix { get; }
}

public sealed class DestinationConfig
{
    public DestinationConfig(string id, Uri address)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        Address = address ?? throw new ArgumentNullException(nameof(address));
    }

    public string Id { get; }

    public Uri Address { get; }
}

public sealed class ClusterConfig
{
    public ClusterConfig(string id, IReadOnlyList<DestinationConfig> destinations)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        Destinations = destinations ?? throw new ArgumentNullException(nameof(destinations));
    }

    public string Id { get; }

    public IReadOnlyList<DestinationConfig> Destinations { get; }
}

public sealed class ProxyConfig
{
    private readonly IReadOnlyList<RouteConfig> _routes;
    private readonly IReadOnlyDictionary<string, ClusterConfig> _clusters;

    public ProxyConfig(IReadOnlyList<RouteConfig> routes, IReadOnlyList<ClusterConfig> clusters)
    {
        _routes = routes ?? throw new ArgumentNullException(nameof(routes));
        _clusters = (clusters ?? throw new ArgumentNullException(nameof(clusters)))
            .ToDictionary(cluster => cluster.Id, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<RouteConfig> Routes => _routes;

    public bool TryGetCluster(string clusterId, out ClusterConfig cluster)
    {
        return _clusters.TryGetValue(clusterId, out cluster!);
    }
}

public interface IProxyConfigProvider
{
    ProxyConfig Config { get; }
}

public sealed class StaticProxyConfigProvider : IProxyConfigProvider
{
    public StaticProxyConfigProvider(ProxyConfig config)
    {
        Config = config ?? throw new ArgumentNullException(nameof(config));
    }

    public ProxyConfig Config { get; }
}

public sealed class EnvironmentProxyConfigProvider : IProxyConfigProvider
{
    public EnvironmentProxyConfigProvider(IConfiguration configuration)
    {
        Config = ProxyConfigFactory.Create(configuration);
    }

    public ProxyConfig Config { get; }
}

public static class ProxyConfigFactory
{
    private static readonly string[] DefaultGrpcBackends =
    [
        "http://localhost:7101",
        "http://localhost:7102"
    ];

    public static ProxyConfig Create(IConfiguration configuration)
    {
        var httpBackend = configuration["PROXY_HTTP_BACKEND"] ?? "http://localhost:7001";
        var grpcBackendsValue = configuration["PROXY_GRPC_BACKENDS"];
        var grpcBackends = string.IsNullOrWhiteSpace(grpcBackendsValue)
            ? DefaultGrpcBackends
            : grpcBackendsValue.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var httpDestination = new DestinationConfig("api-1", new Uri(httpBackend));
        var grpcDestinations = grpcBackends
            .Select((address, index) => new DestinationConfig($"grpc-{index + 1}", new Uri(address)))
            .ToArray();

        var routes = new[]
        {
            new RouteConfig("/api/", "api", ProxyRouteKind.Http),
            new RouteConfig("/grpc/", "grpc", ProxyRouteKind.Grpc, stripPrefix: true)
        };

        var clusters = new[]
        {
            new ClusterConfig("api", new[] { httpDestination }),
            new ClusterConfig("grpc", grpcDestinations)
        };

        return new ProxyConfig(routes, clusters);
    }
}
