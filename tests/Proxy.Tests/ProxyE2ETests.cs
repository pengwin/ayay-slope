using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.Json;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Proxy.Configuration;
using Proxy.Tests.Protos;
using Proxy;

namespace Proxy.Tests;

public sealed class ProxyE2ETests
{
    static ProxyE2ETests()
    {
        AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);
    }

    [Fact]
    public async Task Http_requests_flow_through_proxy()
    {
        await using var apiBackend = await StartHttpBackendAsync();
        var proxyConfig = BuildConfig(apiBackend.Address, new[] { apiBackend.Address });

        await using var proxy = await StartProxyAsync(proxyConfig);
        using var client = CreateHttpClient(proxy.Address);

        var response = await client.GetAsync("/api/hello");
        var debugBody = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, debugBody);

        var payload = JsonSerializer.Deserialize<MessageResponse>(debugBody, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });
        Assert.NotNull(payload);
        Assert.Equal("hello from backend", payload!.Message);
    }

    [Fact]
    public async Task Grpc_calls_are_load_balanced_round_robin()
    {
        await using var grpcBackendOne = await StartGrpcBackendAsync("backend-a");
        await using var grpcBackendTwo = await StartGrpcBackendAsync("backend-b");
        await using var httpBackend = await StartHttpBackendAsync();

        var proxyConfig = BuildConfig(httpBackend.Address, new[] { grpcBackendOne.Address, grpcBackendTwo.Address });
        await using var proxy = await StartProxyAsync(proxyConfig);

        var grpcAddress = new Uri(proxy.Address, "/grpc/");
        using var channel = GrpcChannel.ForAddress(grpcAddress, new GrpcChannelOptions
        {
            HttpHandler = CreateGrpcHandler()
        });

        var client = new Greeter.GreeterClient(channel);
        var responses = new List<string>();
        for (var i = 0; i < 6; i++)
        {
            var reply = await client.SayHelloAsync(new HelloRequest { Name = $"test-{i}" });
            responses.Add(reply.Message);
        }

        Assert.Contains(responses, message => message.Contains("backend-a", StringComparison.Ordinal));
        Assert.Contains(responses, message => message.Contains("backend-b", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Health_endpoints_are_served_locally()
    {
        await using var backend = await StartHttpBackendAsync();
        var proxyConfig = BuildConfig(backend.Address, new[] { backend.Address });
        await using var proxy = await StartProxyAsync(proxyConfig);

        using var client = CreateHttpClient(proxy.Address);

        var live = await client.GetFromJsonAsync<HealthResponse>("/health/live");
        Assert.NotNull(live);
        Assert.Equal("live", live!.Status);

        var ready = await client.GetFromJsonAsync<HealthResponse>("/health/ready");
        Assert.NotNull(ready);
        Assert.Equal("ready", ready!.Status);
    }

    private static ProxyConfig BuildConfig(Uri httpBackend, IReadOnlyList<Uri> grpcBackends)
    {
        var routes = new[]
        {
            new RouteConfig("/api/", "api", ProxyRouteKind.Http),
            new RouteConfig("/grpc/", "grpc", ProxyRouteKind.Grpc, stripPrefix: true)
        };

        var httpCluster = new ClusterConfig("api", new[]
        {
            new DestinationConfig("api-1", httpBackend)
        });

        var grpcDestinations = grpcBackends
            .Select((uri, index) => new DestinationConfig($"grpc-{index + 1}", uri))
            .ToArray();

        var grpcCluster = new ClusterConfig("grpc", grpcDestinations);
        return new ProxyConfig(routes, new[] { httpCluster, grpcCluster });
    }

    private static async Task<RunningHost> StartHttpBackendAsync()
    {
        var port = PortAllocator.GetFreePort();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.ListenLocalhost(port, listenOptions =>
            {
                listenOptions.Protocols = HttpProtocols.Http1;
            });
        });

        var app = builder.Build();
        app.MapGet("/api/hello", () => Results.Json(new MessageResponse("hello from backend")));
        await app.StartAsync();
        return new RunningHost(app, new Uri($"http://127.0.0.1:{port}"));
    }

    private static async Task<RunningHost> StartGrpcBackendAsync(string instanceId)
    {
        var port = PortAllocator.GetFreePort();
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddGrpc();
        builder.Services.AddSingleton<TestGreeterService>(_ => new TestGreeterService(instanceId));
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.ListenLocalhost(port, listenOptions =>
            {
                listenOptions.Protocols = HttpProtocols.Http2;
            });
        });

        var app = builder.Build();
        app.MapGrpcService<TestGreeterService>();
        app.MapGet("/", () => Results.Text("grpc backend"));
        await app.StartAsync();
        return new RunningHost(app, new Uri($"http://127.0.0.1:{port}"));
    }

    private static async Task<RunningHost> StartProxyAsync(ProxyConfig config)
    {
        var port = PortAllocator.GetFreePort();
        var builder = WebApplication.CreateBuilder();
        ProxyHost.ConfigureServices(builder, new ProxyHostOptions
        {
            Address = IPAddress.Loopback,
            Port = port
        }, config);

        var app = builder.Build();
        ProxyHost.ConfigurePipeline(app);
        await app.StartAsync();
        return new RunningHost(app, new Uri($"https://127.0.0.1:{port}"));
    }

    private static HttpClient CreateHttpClient(Uri baseAddress)
    {
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };

        return new HttpClient(handler) { BaseAddress = baseAddress };
    }

    private static HttpMessageHandler CreateGrpcHandler()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.None
        };

        var sslOptions = handler.SslOptions;
        sslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        sslOptions.EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;
        sslOptions.ApplicationProtocols ??= new List<SslApplicationProtocol>();
        sslOptions.ApplicationProtocols.Add(SslApplicationProtocol.Http2);
        return handler;
    }

    private sealed class RunningHost : IAsyncDisposable
    {
        private readonly WebApplication _app;

        public RunningHost(WebApplication app, Uri address)
        {
            _app = app;
            Address = address;
        }

        public Uri Address { get; }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    private sealed class TestGreeterService : Greeter.GreeterBase
    {
        private readonly string _instanceId;

        public TestGreeterService(string instanceId)
        {
            _instanceId = instanceId;
        }

        public override Task<HelloReply> SayHello(HelloRequest request, Grpc.Core.ServerCallContext context)
        {
            return Task.FromResult(new HelloReply
            {
                Message = $"Hello from {_instanceId}"
            });
        }
    }

    private sealed record MessageResponse(string Message);

    private sealed record HealthResponse(string Status);

    private static class PortAllocator
    {
        public static int GetFreePort()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }
    }
}
