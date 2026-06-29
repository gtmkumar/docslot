using System.Threading.RateLimiting;
using mediq.Api.Extensions;
using mediq.Api.Middleware;
using mediq.Api.Authorization;
using mediq.Application;
using mediq.Application.Options;
using mediq.Infrastructure;
using mediq.Utilities.Middlewares.ExceptionsMiddleware;
using Microsoft.AspNetCore.RateLimiting;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// --- Serilog structured logging (enriched with correlation id via the log scope + trace span) ---
builder.Host.UseSerilog((context, services, config) => config
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("Service", context.HostingEnvironment.ApplicationName)
    .WriteTo.Console(formatProvider: System.Globalization.CultureInfo.InvariantCulture));

// --- Aspire ServiceDefaults: OpenTelemetry (traces/metrics/logs), health checks, service discovery ---
builder.AddServiceDefaults();

// --- Options bound from configuration / Aspire (never hardcoded) ---
builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.SectionName));
builder.Services.Configure<AuthPolicyOptions>(builder.Configuration.GetSection(AuthPolicyOptions.SectionName));
builder.Services.Configure<EncryptionOptions>(builder.Configuration.GetSection(EncryptionOptions.SectionName));
builder.Services.Configure<WhatsAppOptions>(builder.Configuration.GetSection(WhatsAppOptions.SectionName));

// --- Application (custom CQRS + behaviors) and Infrastructure (EF Core, security, RBAC, audit) ---
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);

// --- WhatsApp outbound drain worker (delivers docslot.outbox_messages). Enabled by default; toggle off
// via WhatsApp:OutboxWorkerEnabled=false for API-only / read-only instances. The sender (stub vs real Meta)
// is selected inside AddInfrastructure by whether an access token is configured. ---
var whatsAppSection = builder.Configuration.GetSection(WhatsAppOptions.SectionName);
if (whatsAppSection.GetValue("OutboxWorkerEnabled", true))
    builder.Services.AddHostedService<mediq.Api.Workers.OutboxDrainWorker>();

// --- Booking maintenance worker: materializes a rolling horizon of bookable time_slots from doctor
// schedules + sweeps stale slot holds. Enabled by default; toggle via Booking:MaintenanceWorkerEnabled=false. ---
if (builder.Configuration.GetValue("Booking:MaintenanceWorkerEnabled", true))
    builder.Services.AddHostedService<mediq.Api.Workers.BookingMaintenanceWorker>();

// --- Webhook delivery worker: drains platform_api.webhook_deliveries out-of-band (publish enqueues only, so a
// slow/dead subscriber never blocks the request path). Enabled by default; toggle via Webhooks:DeliveryWorkerEnabled=false. ---
if (builder.Configuration.GetValue("Webhooks:DeliveryWorkerEnabled", true))
    builder.Services.AddHostedService<mediq.Api.Workers.WebhookDeliveryWorker>();

// --- Integration-event drain worker: publishes platform_api.integration_event_outbox rows to the message
// broker out-of-band. DEFAULT-OFF (Messaging:DrainWorkerEnabled=false) — the broker + consumer are deferred,
// so the outbox safely CAPTURES every event but does not drain until explicitly enabled. When the provider is
// 'rabbitmq', also register the Aspire RabbitMQ client (the IConnection the RabbitMqIntegrationEventBus needs). ---
if (string.Equals(builder.Configuration["Messaging:Provider"], "rabbitmq", StringComparison.OrdinalIgnoreCase))
    builder.AddRabbitMQClient("rabbitmq");   // Aspire: IConnection singleton + health/OTel, from the "rabbitmq" connection string
if (builder.Configuration.GetValue("Messaging:DrainWorkerEnabled", false))
    builder.Services.AddHostedService<mediq.Api.Workers.IntegrationEventDrainWorker>();

// --- Retention pruner worker: physically deletes AGED terminal status='success' rows from the two append-only
// platform_api operational tables (integration_event_outbox / webhook_deliveries) on a slow sweep, closing the
// unbounded-growth ops hazard. DEFAULT-OFF (Retention:PrunerEnabled=false) — physical deletion is opt-in;
// 'failed'/'pending'/'processing' are never deleted and 'abandoned' dead-letters are kept (forensic). ---
if (builder.Configuration.GetValue("Retention:PrunerEnabled", false))
    builder.Services.AddHostedService<mediq.Api.Workers.RetentionPruneWorker>();

// --- Cross-cutting / web ---
builder.Services.AddRequestContext();
builder.Services.AddPlatformJwtAuth(builder.Configuration);
builder.Services.AddPlatformAuthorization();
builder.Services.AddProblemDetails();

builder.Services.AddControllers();
builder.Services.AddOpenApi();   // .NET 10 native OpenAPI (Swashbuckle is incompatible with ASP.NET Core 10)

// --- Rate limiting (defense in depth; the gateway also rate-limits at the edge) ---
// Production: 100 req/min/IP. Development: a much higher cap so local SPA work and
// automated screenshot/QA sweeps (which re-fetch /me + menus + permissions per
// navigation) don't trip 429s. The edge gateway still enforces the strict limit.
var rateLimitPerMinute = builder.Environment.IsDevelopment() ? 5000 : 100;
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = rateLimitPerMinute, Window = TimeSpan.FromMinutes(1) }));
});

var app = builder.Build();

// --- Pipeline order matters ---
app.UseMiddleware<ExceptionHandler>();   // reuse Utilities global exception → ApiResponse envelope (DRY)
app.UseCorrelationId();                   // honor/generate X-Correlation-ID, push into log scope

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();   // serves the OpenAPI document at /openapi/v1.json
}

app.UseSerilogRequestLogging();
app.UseRateLimiter();

app.UseAuthentication();
app.UseScopeResolution();                 // client-token: resolve scopes once + verify token not revoked (platform_api)
app.UseApiClientRequestLog();             // log client requests to api_requests + per-client rate limit
app.UsePermissionResolution();            // user-token: resolve-once-per-request permission set (NFR-PERF-01)
app.UseAuthorization();

app.MapControllers();
app.MapDefaultEndpoints();                // /health + /alive from ServiceDefaults

app.Run();

/// <summary>Exposed so the integration test's WebApplicationFactory can boot the real pipeline.</summary>
public partial class Program;
