// .NET Aspire AppHost — orchestrates the platform_core slice and wires service discovery + config.
//
// The canonical PostgreSQL database (docslot_platform) is provisioned externally (homebrew, all 113
// tables, ADR-007 SQL-is-truth), so we reference it as an existing connection string rather than letting
// Aspire spin a container that would NOT have the schema. The Api consumes it via the "platform-db" name.
//
// RabbitMQ + the AI sibling service are registered as placeholders to be filled in later slices.

var builder = DistributedApplication.CreateBuilder(args);

// Existing canonical Postgres (Username gtmkumar, homebrew trust auth, no password).
var platformDb = builder.AddConnectionString("platform-db");

// The transactional API (system of record). Gets the DB connection string by service-discovery name.
var api = builder.AddProject<Projects.mediq_Api>("mediq-api")
    .WithReference(platformDb)
    .WithExternalHttpEndpoints();

// YARP gateway — the trust boundary (JWT + rate limiting at the edge), routes to the API.
builder.AddProject<Projects.mediq_Gateway>("mediq-gateway")
    .WithReference(api)
    .WithExternalHttpEndpoints();

// Placeholders for later slices:
//   var rabbit = builder.AddRabbitMQ("rabbitmq");                 // slice: integration events
//   builder.AddPythonApp("docslot-ai", ...).WithReference(rabbit); // slice 06: AI sibling

builder.Build().Run();
