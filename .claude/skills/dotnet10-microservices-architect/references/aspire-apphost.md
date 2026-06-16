# .NET Aspire — AppHost, ServiceDefaults, Discovery, Health, OpenTelemetry

`Aspire/AppHost` is **service 5**: the orchestrator that wires every service, the gateway, PostgreSQL,
and RabbitMQ together for local dev and produces the deployment manifest. `Aspire/ServiceDefaults` is the
shared library every service references to get OTel, health checks, and service discovery for free.

## Contents
- [ServiceDefaults](#servicedefaults)
- [AppHost orchestration](#apphost-orchestration)
- [How services consume injected config](#how-services-consume-injected-config)
- [Health checks](#health-checks)
- [OpenTelemetry](#opentelemetry)

## ServiceDefaults

Every `*.Api` and the gateway call `builder.AddServiceDefaults()` and `app.MapDefaultEndpoints()`. This
is where observability and discovery are bootstrapped so individual services stay clean.

```csharp
// Aspire/ServiceDefaults/Extensions.cs
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

public static class ServiceDefaultsExtensions
{
    public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        builder.ConfigureOpenTelemetry();
        builder.AddDefaultHealthChecks();

        builder.Services.AddServiceDiscovery();
        builder.Services.ConfigureHttpClientDefaults(http =>
        {
            http.AddStandardResilienceHandler();   // retries, circuit breaker, timeout
            http.AddServiceDiscovery();
        });
        return builder;
    }

    public static TBuilder ConfigureOpenTelemetry<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        builder.Logging.AddOpenTelemetry(o =>
        {
            o.IncludeFormattedMessage = true;
            o.IncludeScopes = true;
        });

        builder.Services.AddOpenTelemetry()
            .WithMetrics(m => m
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation())
            .WithTracing(t => t
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddNpgsql()                       // EF/Npgsql spans
                .AddSource("BuildingBlocks.Messaging")); // custom RabbitMQ activity source

        // OTLP exporter — endpoint injected by Aspire (points at the dashboard locally).
        if (!string.IsNullOrEmpty(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
            builder.Services.AddOpenTelemetry().UseOtlpExporter();

        return builder;
    }

    public static TBuilder AddDefaultHealthChecks<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        builder.Services.AddHealthChecks()
            .AddCheck("self", () => HealthCheckResult.Healthy(), ["live"]);
        return builder;
    }

    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        // /health = all checks (readiness); /alive = liveness only.
        app.MapHealthChecks("/health");
        app.MapHealthChecks("/alive", new()
        {
            Predicate = r => r.Tags.Contains("live")
        });
        return app;
    }
}
```

## AppHost orchestration

The AppHost is C# that declares the topology. Aspire injects connection strings and service URLs into
each project via environment variables / config, so nothing hardcodes hosts or ports.

```csharp
// Aspire/AppHost/Program.cs
var builder = DistributedApplication.CreateBuilder(args);

// --- Backing services ---
var postgres = builder.AddPostgres("postgres")
    .WithDataVolume()                       // persist across restarts
    .WithPgAdmin();                         // optional admin UI

var orderingDb = postgres.AddDatabase("ordering-db");
var catalogDb  = postgres.AddDatabase("catalog-db");
var identityDb = postgres.AddDatabase("identity-db");

var rabbitmq = builder.AddRabbitMQ("rabbitmq")
    .WithManagementPlugin()                 // RabbitMQ management UI
    .WithDataVolume();

// --- Microservices (3) ---
var ordering = builder.AddProject<Projects.Ordering_Api>("ordering-api")
    .WithReference(orderingDb)              // injects ConnectionStrings:ordering-db
    .WithReference(rabbitmq)                // injects ConnectionStrings:rabbitmq
    .WaitFor(orderingDb)
    .WaitFor(rabbitmq);

var catalog = builder.AddProject<Projects.Catalog_Api>("catalog-api")
    .WithReference(catalogDb)
    .WithReference(rabbitmq)
    .WaitFor(catalogDb)
    .WaitFor(rabbitmq);

var identity = builder.AddProject<Projects.Identity_Api>("identity-api")
    .WithReference(identityDb)
    .WaitFor(identityDb);

// --- Gateway (service 4) — references the three services for discovery ---
builder.AddProject<Projects.Gateway_Yarp>("gateway")
    .WithReference(ordering)
    .WithReference(catalog)
    .WithReference(identity)
    .WithExternalHttpEndpoints();           // the only publicly exposed endpoint

builder.Build().Run();
```

> **`WithReference` is the magic.** `WithReference(orderingDb)` makes `ConnectionStrings:ordering-db`
> available to the Ordering service — which is exactly the key its `AddDbContext` reads
> (`references/data-efcore.md`). `WithReference(ordering)` on the gateway registers the service name for
> discovery so `http://ordering-api` resolves. You never manage ports or `.env` files.

The `Projects.*` types are generated by Aspire when you add a project reference from AppHost to each
service `.csproj`. Add them with:

```bash
dotnet add Aspire/AppHost reference src/Services/Ordering/Ordering.Api
# repeat for catalog, identity, gateway
```

## How services consume injected config

In each service's `Program.cs`:

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();                       // OTel + health + discovery

// Aspire injects these connection strings; no hardcoded hosts.
builder.Services.AddOrderingInfrastructure(builder.Configuration);
builder.AddNpgsqlDbContext<OrderingDbContext>("ordering-db");  // alternative Aspire helper
builder.AddRabbitMQClient("rabbitmq");              // registers IConnection for the event bus

builder.Services.AddOrderingApplication();          // AddCqrs(...) etc.
builder.Services.AddControllers();
builder.Services.AddOpenApi();

var app = builder.Build();
app.UseExceptionHandling();                         // BuildingBlocks.Web
app.UseCorrelationId();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapOpenApi();
app.MapDefaultEndpoints();
app.Run();
```

## Health checks

Liveness vs readiness matters in orchestration: `/alive` answers "is the process up?" (cheap, no
dependencies), `/health` answers "can it serve traffic?" (checks DB, broker). Add dependency checks in
each service:

```csharp
builder.Services.AddHealthChecks()
    .AddNpgSql(builder.Configuration.GetConnectionString("ordering-db")!, name: "ordering-db", tags: ["ready"])
    .AddRabbitMQ(name: "rabbitmq", tags: ["ready"]);
```

The Aspire dashboard surfaces all health states; YARP can route only to healthy destinations using YARP's
active health checks if you enable them per cluster.

## OpenTelemetry

OTel is configured once in `ServiceDefaults` and flows everywhere automatically: ASP.NET Core request
traces, HttpClient spans (including gateway→service hops, correlated by trace context), Npgsql query
spans, and custom RabbitMQ publish/consume spans via the `BuildingBlocks.Messaging` activity source. The
OTLP exporter targets the Aspire dashboard locally; in production point `OTEL_EXPORTER_OTLP_ENDPOINT` at
your collector (Jaeger, Tempo, Honeycomb, etc.).

To emit custom spans from the message bus, define an `ActivitySource`:

```csharp
// BuildingBlocks.Messaging/Diagnostics.cs
public static class Diagnostics
{
    public static readonly ActivitySource Source = new("BuildingBlocks.Messaging");
}
// In RabbitMqEventBus.PublishAsync:
using var activity = Diagnostics.Source.StartActivity($"publish {routingKey}", ActivityKind.Producer);
activity?.SetTag("messaging.system", "rabbitmq");
activity?.SetTag("messaging.destination", RabbitMqEventBus.ExchangeName);
```

> Trace context propagation across RabbitMQ requires injecting/extracting the W3C `traceparent` into
> message headers on publish and restoring it on consume. Add the propagator calls in the bus and consumer
> so a trace spans the full async flow — this is what makes the Aspire/Jaeger waterfall show the order
> placement *and* the downstream inventory reservation as one trace.
