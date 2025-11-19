using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Proxy.Configuration;
using Proxy.Forwarding;
using Proxy.LoadBalancing;
using Proxy.Routing;
using Proxy.Infrastructure;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Linq;

namespace Proxy;

public sealed class ProxyHostOptions
{
    public IPAddress Address { get; init; } = IPAddress.Any;

    public int? Port { get; init; }

    public X509Certificate2? Certificate { get; init; }

    public bool? EnableTls { get; init; }
}

public static class ProxyHost
{
    private const string Http2Switch = "System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport";

    public static void ConfigureServices(
        WebApplicationBuilder builder,
        ProxyHostOptions? hostOptions = null,
        ProxyConfig? configOverride = null)
    {
        AppContext.SetSwitch(Http2Switch, true);

        var listenAddress = hostOptions?.Address ?? IPAddress.Any;
        var listenPort = hostOptions?.Port ?? builder.Configuration.GetValue<int>("PROXY_PORT", 5000);
        var enableTls = hostOptions?.EnableTls ?? builder.Configuration.GetValue<bool>("PROXY_ENABLE_TLS", true);
        var certificate = enableTls ? hostOptions?.Certificate ?? DevelopmentCertificate.Instance : null;

        builder.WebHost.ConfigureKestrel(options =>
        {
            var endpointProtocols = enableTls ? HttpProtocols.Http1AndHttp2 : HttpProtocols.Http2;

            options.ConfigureEndpointDefaults(listenOptions =>
            {
                listenOptions.Protocols = endpointProtocols;
            });

            options.Listen(listenAddress, listenPort, listenOptions =>
            {
                listenOptions.Protocols = endpointProtocols;
                if (enableTls && certificate is not null)
                {
                    listenOptions.UseHttps(certificate);
                }
            });
        });

        builder.Services.AddHttpClient("proxy", client =>
            {
                client.DefaultRequestVersion = HttpVersion.Version20;
                client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
                client.Timeout = Timeout.InfiniteTimeSpan;
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.None,
                UseCookies = false
            });

        if (configOverride is not null)
        {
            builder.Services.AddSingleton<IProxyConfigProvider>(new StaticProxyConfigProvider(configOverride));
        }
        else
        {
            builder.Services.AddSingleton<IProxyConfigProvider>(sp => new EnvironmentProxyConfigProvider(builder.Configuration));
        }

        builder.Services.AddSingleton<RouteMatcher>();
        builder.Services.AddSingleton<ILoadBalancer, RoundRobinLoadBalancer>();
        builder.Services.AddScoped<ProxyRequestExecutor>();
    }

    public static void ConfigurePipeline(WebApplication app)
    {
        app.MapGet("/health/live", () => Results.Json(new { status = "live" }));
        app.MapGet("/health/ready", () => Results.Json(new { status = "ready" }));

        app.Map("/{**catch-all}", async context =>
        {
            var loggerFactory = context.RequestServices.GetRequiredService<ILoggerFactory>();
            var logger = loggerFactory.CreateLogger("Proxy.Request");
            var matcher = context.RequestServices.GetRequiredService<RouteMatcher>();
            var configProvider = context.RequestServices.GetRequiredService<IProxyConfigProvider>();
            var loadBalancer = context.RequestServices.GetRequiredService<ILoadBalancer>();
            var executor = context.RequestServices.GetRequiredService<ProxyRequestExecutor>();

            var grpcRoute = configProvider.Config.Routes.FirstOrDefault(route => route.Kind == ProxyRouteKind.Grpc);
            if (grpcRoute is not null && IsGrpcRequest(context.Request) &&
                !context.Request.Path.StartsWithSegments(grpcRoute.PathPrefix, StringComparison.OrdinalIgnoreCase))
            {
                context.Request.Path = grpcRoute.PathPrefix.Add(context.Request.Path);
            }

            var match = matcher.Match(context.Request.Path);
            if (match is null)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                logger.LogWarning("No route for path {Path}", context.Request.Path);
                await context.Response.WriteAsync("No matching route");
                return;
            }

            if (!configProvider.Config.TryGetCluster(match.Route.ClusterId, out var cluster) || cluster.Destinations.Count == 0)
            {
                context.Response.StatusCode = StatusCodes.Status502BadGateway;
                await context.Response.WriteAsync("Cluster unavailable");
                return;
            }

            var destination = loadBalancer.PickDestination(cluster, context);
            logger.LogDebug("Forwarding {Method} {Path} to {Destination}", context.Request.Method, context.Request.Path, destination.Address);
            await executor.ForwardAsync(context, match, destination);
        });
    }

    private static bool IsGrpcRequest(HttpRequest request)
    {
        if (!string.Equals(request.Protocol, HttpProtocol.Http2, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (request.ContentType is null)
        {
            return false;
        }

        return request.ContentType.StartsWith("application/grpc", StringComparison.OrdinalIgnoreCase);
    }
}
