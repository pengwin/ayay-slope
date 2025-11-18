You are a senior .NET backend engineer.

Your task: build a minimal but production-inspired **HTTP reverse proxy** service in **.NET 9 (ASP.NET Core)** that:

- is implemented **from scratch (no YARP / no reverse-proxy libraries)**,
- can forward both regular HTTP requests and **gRPC** calls,
- supports simple **round-robin load balancing** for a gRPC backend cluster,
- includes **end-to-end (E2E) tests** that exercise the proxy against real in-memory HTTP and gRPC backends.

Keep the feature set focused and small. Do not add advanced features like auth, rate limiting, complex config systems, etc.

---

## High-level goals

1. Implement a small reverse proxy in **C# / ASP.NET Core** using the minimal hosting model.
2. Manually handle:
   - route matching,
   - backend cluster selection,
   - destination selection (round robin),
   - request/response forwarding.
3. Support:
   - HTTP/1.1 for normal HTTP APIs,
   - HTTP/2 for gRPC traffic.
4. Provide E2E tests that:
   - spin up both the proxy and simple backend services in-process, and
   - verify that HTTP and gRPC calls successfully flow through the proxy.

---

## Constraints & what NOT to do

- **Do NOT use YARP** (`Yarp.ReverseProxy`) or any other reverse-proxy/load-balancing framework.
- Do NOT use Envoy, Nginx, or any external proxy binary in tests.
- Only use:
  - ASP.NET Core,
  - `HttpClient` / `HttpClientFactory`,
  - `Grpc.AspNetCore` and `Grpc.Net.Client` for gRPC,
  - standard .NET libraries and a common test framework.
- Keep configuration **simple, in code** (a small in-memory configuration model is enough; full config binding is not required).

---

## Tech stack

- Language: **C#** (modern features, `nullable` enabled).
- Framework: **.NET 9**, TargetFramework `net9.0`.
- Web framework: **ASP.NET Core minimal hosting model** (top-level `Program.cs`).
- gRPC implementation: `Grpc.AspNetCore` (for backend gRPC services in tests) and `Grpc.Net.Client` (for clients in tests).
- Testing: a standard .NET testing framework, such as **xUnit** (integration/E2E style tests).
- Logging: built-in `Microsoft.Extensions.Logging`.

---

## Functional requirements

### 1. Basic reverse proxy

The reverse proxy must:

- Listen on a configurable port (default: **5000**).
- Enable **HTTP/1.1 and HTTP/2** on the same listener.
- Expose two main routes:

  1. `"/api/{**catch-all}"` → single HTTP backend.
     - For simple JSON/HTTP APIs.
  2. `"/grpc/{**catch-all}"` → gRPC backend cluster.
     - For gRPC calls and should support multiple backend instances with round-robin load balancing.

- For any incoming request:
  - Determine which route matches based on path prefix (`/api/` or `/grpc/`).
  - Resolve the corresponding cluster and pick a destination using a **round-robin** strategy.
  - Forward the request to that destination.
  - Forward the response back to the client.

### 2. HTTP request forwarding logic

Implement HTTP forwarding manually:

- Use `IHttpClientFactory` to get an `HttpClient` configured to support HTTP/2.
- For each incoming request:
  - Create an `HttpRequestMessage` targeting the selected destination URL.
  - Copy:
    - HTTP method,
    - path and query string,
    - headers (excluding hop-by-hop headers like `Connection`, `Transfer-Encoding`, etc.),
    - body (stream the body rather than buffering everything in memory, if possible).
  - Send the request using `HttpClient`.
  - Read the response and:
    - Copy status code,
    - Copy response headers (excluding hop-by-hop headers),
    - Copy the response body stream back to the client.

No caching is required; just forward.

### 3. gRPC-specific behavior

For the `/grpc/**` route:

- The proxy should **preserve HTTP/2**, so that gRPC clients can talk to it as if it was the real server.
- Use `HttpRequestMessage.Version = HttpVersion.Version20` and appropriate `VersionPolicy` so that outgoing calls to gRPC backends use HTTP/2.
- Preserve:
  - gRPC headers/metadata (in HTTP headers),
  - response trailers (important for gRPC status).
- Ensure the proxy works with **unary** gRPC calls. Full streaming support is a bonus, but not strictly required as long as the implementation is compatible and does not intentionally break streams.
- You may assume gRPC in tests runs without TLS (plaintext HTTP/2):
  - Use the standard switch to allow HTTP/2 without TLS for development/tests, e.g.
    `AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);` on the client side.

### 4. Load balancing

Implement minimal in-memory load balancing:

- Define simple domain models for routes, clusters and destinations. For example:
  - a route binding a path prefix to a cluster,
  - a cluster containing a list of destinations,
  - a destination representing a backend base URI.
- Implement an abstraction for load balancing with a method such as:
  - `Destination PickDestination(IReadOnlyList<Destination> destinations, HttpContext httpContext);`
- Implement a **round-robin** load balancer that:
  - Uses an atomic index or locking to cycle through destinations,
  - Is safe for concurrent use.
- For `/grpc/**`, configure a cluster with at least **2 destinations** to demonstrate load balancing.

### 5. Health endpoints

Implement two simple endpoints handled directly by the proxy, not forwarded:

- `GET /health/live` — returns `200 OK` with a simple JSON or text payload (e.g., `{ "status": "live" }`).
- `GET /health/ready` — returns `200 OK` once the proxy is fully initialized.
  - For this task, it is acceptable to consider the proxy “ready” immediately after startup.

---

## Non-functional requirements

- Enable nullable reference types (`<Nullable>enable</Nullable>`).
- Use clean, idiomatic C#.
- Keep the code reasonably small and focused.
- Add comments only where logic may be non-obvious.

---

## E2E test requirements

Use **in-process test servers** to run backends and the proxy simultaneously. You can use `WebApplicationFactory`, `TestServer`, or manual host creation. The tests must send real HTTP/gRPC requests through the proxy and assert on the behavior of both proxy and backends.

### 1. HTTP E2E test

Create at least one test that:

1. Starts a simple HTTP backend:
   - ASP.NET Core app on a dynamic port.
   - Endpoint `GET /api/hello` returning something like `{ "message": "hello from backend" }`.
2. Starts the proxy, configured so that `/api/{**}` routes to that backend.
3. Uses `HttpClient` against the proxy:
   - Sends `GET /api/hello`.
   - Asserts:
     - Status code is 200,
     - Response body matches what the backend returns.

### 2. gRPC unary E2E test

Create at least one gRPC E2E test:

1. Define a simple gRPC service, for example:

   - Service: `Greeter`
   - Method: `SayHello(HelloRequest) returns (HelloReply)`
   - `HelloRequest` has a `string Name`.
   - `HelloReply` has a `string Message`.

2. Create **two** backend gRPC servers (on two different ports):
   - Both implement `Greeter.SayHello`.
   - Each instance returns a response that encodes its own instance ID (e.g., `"Hello from backend-1"` vs `"Hello from backend-2"`).

3. Start the proxy, configured so that:
   - All `/grpc/{**}` traffic routes to a cluster containing both gRPC backends.
   - The proxy uses **round-robin** to pick destinations.

4. Use `Grpc.Net.Client` in the test:
   - Create a channel pointing to the proxy (HTTP/2, plaintext).
   - Call `SayHello` multiple times (e.g., 4–6 times).
   - Assert that:
     - All calls succeed,
     - The responses show that **both** backend instances were used at least once (verifies that the load balancer distributes calls).

### 3. Health endpoint test

Create a test that:

- Starts the proxy.
- Uses `HttpClient` to GET `/health/live` and `/health/ready`.
- Asserts `200 OK` and expected payloads.

---

## Implementation hints

- Configure Kestrel so that HTTP/1.1 and HTTP/2 are both supported on the listening port, e.g. configuring protocols appropriately.
- Register `IHttpClientFactory` and configure it to support HTTP/2 when calling gRPC backends.
- Register your in-memory routing / cluster / destination configuration and the load balancer in DI.
- Implement proxy logic as middleware or endpoint handlers that:
  - Match the path prefix (`/api/` or `/grpc/`),
  - Select the appropriate cluster and destination,
  - Forward the request and return the response.

---

## Output requirements

- Provide all necessary source code (C#, project files, and any required `.proto` definitions) so that the solution can be built and all tests can be executed with standard .NET CLI commands.
- Ensure the code is complete, compilable, and that all described tests are present and can pass.
- Do not include long theoretical explanations; focus on working code and minimal practical comments.
