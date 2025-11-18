# ayay-slope

Minimal reverse proxy built with ASP.NET Core (.NET 9) that forwards regular HTTP traffic and gRPC calls to backend services while applying round-robin load balancing.

## Running the proxy

```bash
dotnet run --project src/Proxy/Proxy.csproj
```

Configure runtime behavior via environment variables:

| Variable | Description | Default |
| --- | --- | --- |
| `PROXY_PORT` | Listening port for the proxy. | `5000` |
| `PROXY_HTTP_BACKEND` | Base URL for the `/api/**` route. | `http://localhost:7001` |
| `PROXY_GRPC_BACKENDS` | Semicolon-separated list of gRPC backend URLs for `/grpc/**`. | `http://localhost:7101;http://localhost:7102` |
| `PROXY_ENABLE_TLS` | Enables HTTPS termination (required for HTTP/2 by default). | `true` |

Both HTTP/1.1 and HTTP/2 are enabled on the same listener (HTTPS by default) so that gRPC clients can connect using HTTP/2.

### Running without TLS (local dev)

For quick localhost experiments you can disable HTTPS and serve plaintext HTTP/1.1+HTTP/2 on the same port. Set `PROXY_ENABLE_TLS=false` before launching the proxy:

```bash
set -gx PROXY_ENABLE_TLS false
dotnet run --project src/Proxy/Proxy.csproj
```

When talking to the proxy over HTTP/2 without TLS, .NET clients must also allow unencrypted HTTP/2:

```csharp
AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);
```

Remember to re-enable TLS (`PROXY_ENABLE_TLS=true`) before running tests or using real gRPC clients that expect HTTPS.

## Tests

End-to-end coverage spins up in-memory HTTP and gRPC backends plus the proxy. Run the suite with:

```bash
dotnet test ayay-slope.sln
```