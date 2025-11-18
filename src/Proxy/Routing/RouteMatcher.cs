using System.Linq;
using Microsoft.AspNetCore.Http;
using Proxy.Configuration;

namespace Proxy.Routing;

public sealed class RouteMatcher
{
    private readonly IProxyConfigProvider _configProvider;
    private readonly RouteConfig? _grpcRoute;

    public RouteMatcher(IProxyConfigProvider configProvider)
    {
        _configProvider = configProvider;
        _grpcRoute = configProvider.Config.Routes.FirstOrDefault(route => route.Kind == ProxyRouteKind.Grpc);
    }

    public RouteMatchResult? Match(PathString path)
    {
        foreach (var route in _configProvider.Config.Routes)
        {
            if (path.StartsWithSegments(route.PathPrefix, StringComparison.OrdinalIgnoreCase, out var remainder))
            {
                var downstream = route.StripPrefix ? NormalizePath(remainder) : NormalizePath(path);
                return new RouteMatchResult(route, remainder, downstream);
            }
        }

        return null;
    }

    public RouteMatchResult? MatchGrpcFallback(PathString path)
    {
        if (_grpcRoute is null)
        {
            return null;
        }

        var downstream = NormalizePath(path);
        return new RouteMatchResult(_grpcRoute, path, downstream);
    }

    private static PathString NormalizePath(PathString path)
    {
        return path.HasValue ? path : new PathString("/");
    }
}

public sealed record RouteMatchResult(RouteConfig Route, PathString RemainingPath, PathString DownstreamPath);
