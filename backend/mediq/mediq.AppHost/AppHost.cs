// .NET Aspire AppHost — orchestrates the platform_core slice and wires service discovery + config.
//
// The canonical PostgreSQL database (docslot_platform) is provisioned externally (homebrew, all 113
// tables, ADR-007 SQL-is-truth), so we reference it as an existing connection string rather than letting
// Aspire spin a container that would NOT have the schema. The Api consumes it via the "platform-db" name.
//
// The AI sibling service is registered as a placeholder to be filled in a later slice.

var builder = DistributedApplication.CreateBuilder(args);

// Existing canonical Postgres (Username gtmkumar, homebrew trust auth, no password).
var platformDb = builder.AddConnectionString("platform-db");

// RabbitMQ broker for integration events. Wired so the topology is complete, but the Api keeps
// Messaging:Provider=none by DEFAULT, so it boots WITHOUT a configured/running broker (the durable outbox
// still captures every event; the drain worker + a consumer are deferred). Set Messaging:Provider=rabbitmq
// (+ Messaging:DrainWorkerEnabled=true) to actually publish through this broker.
var rabbit = builder.AddRabbitMQ("rabbitmq");

// The transactional API (system of record). Gets the DB + broker connection strings by service-discovery name.
var api = builder.AddProject<Projects.mediq_Api>("mediq-api")
    .WithReference(platformDb)
    .WithReference(rabbit)
    .WithExternalHttpEndpoints();

// YARP gateway — the trust boundary (JWT + rate limiting at the edge), routes to the API.
builder.AddProject<Projects.mediq_Gateway>("mediq-gateway")
    .WithReference(api)
    .WithExternalHttpEndpoints();

// Placeholder for a later slice (no consumer yet — deferred):
//   builder.AddPythonApp("docslot-ai", ...).WithReference(rabbit); // slice 06: AI sibling (AMQP consumer)

builder.Build().Run();
