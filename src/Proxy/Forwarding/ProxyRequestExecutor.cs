using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using Proxy.Configuration;
using Proxy.Routing;

namespace Proxy.Forwarding;

public sealed class ProxyRequestExecutor
{
    private static readonly HashSet<string> HopByHopHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection",
        "Proxy-Connection",
        "Keep-Alive",
        "Transfer-Encoding",
        "Upgrade",
        "Proxy-Authenticate",
        "Proxy-Authorization",
        "Trailer",
        "Host"
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ProxyRequestExecutor> _logger;

    public ProxyRequestExecutor(IHttpClientFactory httpClientFactory, ILogger<ProxyRequestExecutor> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task ForwardAsync(HttpContext context, RouteMatchResult match, DestinationConfig destination)
    {
        var client = _httpClientFactory.CreateClient("proxy");
        using var requestMessage = CreateProxyHttpRequest(context, match, destination);

        try
        {
            using var responseMessage = await client.SendAsync(
                requestMessage,
                HttpCompletionOption.ResponseHeadersRead,
                context.RequestAborted);

            await CopyProxyHttpResponseAsync(context, responseMessage);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            _logger.LogWarning("Request canceled by client.");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Forwarding to {Destination} failed", destination.Address);
            if (!context.Response.HasStarted)
            {
                context.Response.StatusCode = StatusCodes.Status502BadGateway;
                await context.Response.WriteAsync("Bad Gateway");
            }
        }
    }

    private static HttpRequestMessage CreateProxyHttpRequest(HttpContext context, RouteMatchResult match, DestinationConfig destination)
    {
        var targetUri = BuildTargetUri(destination.Address, match.DownstreamPath, context.Request.QueryString);

        var requestMessage = new HttpRequestMessage
        {
            Method = new HttpMethod(context.Request.Method),
            RequestUri = targetUri,
            Version = match.Route.Kind == ProxyRouteKind.Grpc ? HttpVersion.Version20 : GetVersionFromProtocol(context.Request.Protocol),
            VersionPolicy = match.Route.Kind == ProxyRouteKind.Grpc ? HttpVersionPolicy.RequestVersionOrHigher : HttpVersionPolicy.RequestVersionOrLower
        };

        requestMessage.Headers.Host = destination.Address.Authority;

        if (ShouldIncludeRequestBody(context.Request))
        {
            requestMessage.Content = new StreamContent(context.Request.Body);
        }

        foreach (var header in context.Request.Headers)
        {
            if (!ShouldCopyHeader(header.Key))
            {
                continue;
            }

            if (!requestMessage.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()))
            {
                requestMessage.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
            }
        }

        return requestMessage;
    }

    private static async Task CopyProxyHttpResponseAsync(HttpContext context, HttpResponseMessage responseMessage)
    {
        context.Response.StatusCode = (int)responseMessage.StatusCode;

        foreach (var header in responseMessage.Headers)
        {
            if (!ShouldCopyHeader(header.Key))
            {
                continue;
            }

            context.Response.Headers[header.Key] = header.Value.ToArray();
        }

        if (responseMessage.Content is not null)
        {
            foreach (var header in responseMessage.Content.Headers)
            {
                if (!ShouldCopyHeader(header.Key))
                {
                    continue;
                }

                context.Response.Headers[header.Key] = header.Value.ToArray();
            }

            context.Response.Headers.Remove("transfer-encoding");
            await responseMessage.Content.CopyToAsync(context.Response.Body);
        }

        foreach (var trailer in responseMessage.TrailingHeaders)
        {
            context.Response.AppendTrailer(trailer.Key, new StringValues(trailer.Value.ToArray()));
        }
    }

    private static bool ShouldCopyHeader(string headerName)
    {
        return !HopByHopHeaders.Contains(headerName);
    }

    private static bool ShouldIncludeRequestBody(HttpRequest request)
    {
        if (request.ContentLength > 0 || request.Headers.ContainsKey("Transfer-Encoding"))
        {
            return true;
        }

        var method = request.Method;
        return HttpMethods.IsPost(method) || HttpMethods.IsPut(method) || HttpMethods.IsPatch(method);
    }

    private static Uri BuildTargetUri(Uri destinationBase, PathString downstreamPath, QueryString query)
    {
        var builder = new UriBuilder(destinationBase)
        {
            Path = CombinePaths(destinationBase.AbsolutePath, downstreamPath)
        };

        var queryValue = CombineQueries(destinationBase.Query, query);
        builder.Query = queryValue ?? null;

        return builder.Uri;
    }

    private static string CombinePaths(string basePath, PathString remainder)
    {
        var normalizedBase = string.IsNullOrEmpty(basePath) ? "/" : basePath;
        if (!normalizedBase.EndsWith('/'))
        {
            normalizedBase += "/";
        }

        var remainderValue = remainder.HasValue ? remainder.Value! : string.Empty;
        if (remainderValue.StartsWith('/'))
        {
            remainderValue = remainderValue.TrimStart('/');
        }

        var combined = normalizedBase + remainderValue;
        return combined.Length == 0 ? "/" : combined;
    }

    private static string? CombineQueries(string baseQuery, QueryString appendedQuery)
    {
        var baseValue = string.IsNullOrEmpty(baseQuery) ? string.Empty : baseQuery.TrimStart('?');
        var appendedValue = appendedQuery.HasValue ? appendedQuery.Value!.TrimStart('?') : string.Empty;

        if (string.IsNullOrEmpty(baseValue))
        {
            return string.IsNullOrEmpty(appendedValue) ? null : appendedValue;
        }

        if (string.IsNullOrEmpty(appendedValue))
        {
            return baseValue;
        }

        return $"{baseValue}&{appendedValue}";
    }

    private static Version GetVersionFromProtocol(string protocol)
    {
        return protocol switch
        {
            "HTTP/2" or "HTTP/2.0" => HttpVersion.Version20,
            _ => HttpVersion.Version11
        };
    }
}
