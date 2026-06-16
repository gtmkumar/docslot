---
name: dotnet10-microservices-architect
description: >
  Enterprise-grade .NET 10 / ASP.NET Core microservices architect skill. Use whenever building,
  scaffolding, reviewing, or extending a solution that uses Clean Architecture, a custom CQRS framework
  WITHOUT MediatR (or any mediator library), EF Core 10 with database-first PostgreSQL, YARP API Gateway,
  .NET Aspire AppHost, RabbitMQ event-driven integration, OpenTelemetry, Serilog, and JWT auth with
  refresh tokens. Trigger for: custom ICommand/IQuery dispatchers and CQRS pipeline behaviors, YARP
  routing/rate-limiting/JWT validation, Aspire service discovery, RabbitMQ integration events with
  retry/outbox, a shared database library with Unit of Work + Repository, cross-cutting concerns (global
  exception handling, correlation IDs, audit logging, validation pipeline), or generating a complete
  enterprise solution/folder structure. Activate even when the user says only "scaffold a .NET
  microservice", "custom CQRS without MediatR", "Aspire AppHost", or "RabbitMQ integration events".
---

# .NET 10 Microservices Architect

Authoritative patterns for building **enterprise-ready .NET 10 microservices** with Clean Architecture
and a **custom CQRS framework (no MediatR)**. This skill is the source of truth for structure, naming,
and the non-negotiable architectural rules below. Read the relevant `references/` file before generating
code for that concern — each one contains complete, copy-ready implementations.

## Non-Negotiable Rules

These exist because they are the most common ways an enterprise .NET microservices codebase rots. Honor
them unless the user explicitly overrides.

1. **No MediatR, no mediator package.** CQRS is dispatched through hand-rolled `ICommandDispatcher` /
   `IQueryDispatcher` resolving handlers from DI. If you reach for `MediatR`, `MassTransit.Mediator`, or
   `Brighter`, stop — that defeats the entire point. See `references/cqrs-framework.md`.
2. **Domain layer references nothing.** No EF Core, no ASP.NET, no infrastructure. It owns entities,
   value objects, domain events, and domain exceptions only.
3. **Dependencies point inward.** API → Application → Domain. Infrastructure → Application + Domain.
   Application never references Infrastructure; it defines interfaces that Infrastructure implements.
4. **Database-first, not code-first.** The schema is authoritative. Scaffold entities from an existing
   PostgreSQL 18 database; migrations track drift, they don't define truth. See `references/data-efcore.md`.
5. **Repository + Unit of Work only where they earn their place.** Don't wrap every entity in a generic
   repo. Use repositories for aggregates with non-trivial query logic; let read-side queries hit the
   `DbContext`/projection directly. See `references/data-efcore.md`.
6. **Integration events cross service boundaries; domain events stay inside one.** Never publish a domain
   event to RabbitMQ directly — translate it to an integration event at the Application boundary. See
   `references/messaging-rabbitmq.md`.
7. **The gateway is the trust boundary.** YARP validates JWTs and enforces rate limits before requests
   reach a downstream service. Services still validate, but defense-in-depth starts at the edge. See
   `references/yarp-gateway.md` and `references/security-jwt.md`.

## Solution Topology

The target solution is **5 deployable units**: 3 business microservices + 1 YARP gateway + 1 Aspire
AppHost orchestrator. Plus shared libraries that are *not* independently deployed.

```
EnterprisePlatform.sln
│
├── src/
│   ├── Services/
│   │   ├── Ordering/                      ← Microservice 1 (sample bounded context)
│   │   │   ├── Ordering.Domain/
│   │   │   ├── Ordering.Application/
│   │   │   ├── Ordering.Infrastructure/
│   │   │   └── Ordering.Api/
│   │   ├── Catalog/                       ← Microservice 2
│   │   │   ├── Catalog.Domain/
│   │   │   ├── Catalog.Application/
│   │   │   ├── Catalog.Infrastructure/
│   │   │   └── Catalog.Api/
│   │   └── Identity/                      ← Microservice 3 (issues JWT + refresh tokens)
│   │       ├── Identity.Domain/
│   │       ├── Identity.Application/
│   │       ├── Identity.Infrastructure/
│   │       └── Identity.Api/
│   │
│   ├── Gateway/
│   │   └── Gateway.Yarp/                  ← Service 4: YARP reverse proxy / edge
│   │
│   ├── Aspire/
│   │   ├── AppHost/                       ← Service 5: .NET Aspire orchestrator
│   │   └── ServiceDefaults/               ← shared Aspire extensions (OTel, health, discovery)
│   │
│   └── BuildingBlocks/                    ← shared libraries (referenced, not deployed)
│       ├── BuildingBlocks.Cqrs/           ← custom CQRS: ICommand, IQuery, dispatchers, behaviors
│       ├── BuildingBlocks.Domain/         ← Entity base, AggregateRoot, IDomainEvent, ValueObject
│       ├── BuildingBlocks.Messaging/      ← RabbitMQ abstractions, IntegrationEvent, IEventBus
│       ├── BuildingBlocks.Persistence/    ← shared DB library: DbContext base, IUnitOfWork, audit
│       └── BuildingBlocks.Web/            ← exception middleware, correlation ID, ProblemDetails
│
└── tests/
    ├── Ordering.UnitTests/
    ├── Ordering.IntegrationTests/
    └── ArchitectureTests/                 ← NetArchTest rules enforcing the dependency arrows above
```

### Per-service Clean Architecture layout

Every service follows the same four-layer shape. Example for `Ordering`:

```
Ordering.Domain/
├── Entities/            (Order, OrderItem — AggregateRoot derivatives)
├── ValueObjects/        (Address, Money)
├── Events/              (OrderPlacedDomainEvent : IDomainEvent)
├── Enums/
└── Exceptions/          (OrderNotFoundException : DomainException)

Ordering.Application/
├── Abstractions/        (IOrderRepository, IUnitOfWork — interfaces only)
├── Commands/
│   └── PlaceOrder/      (PlaceOrderCommand, PlaceOrderCommandHandler, PlaceOrderValidator)
├── Queries/
│   └── GetOrderById/    (GetOrderByIdQuery, GetOrderByIdQueryHandler)
├── Dtos/
├── IntegrationEvents/   (OrderPlacedIntegrationEvent + handlers for inbound events)
└── DependencyInjection.cs

Ordering.Infrastructure/
├── Persistence/
│   ├── OrderingDbContext.cs
│   ├── Configurations/  (IEntityTypeConfiguration<T> per entity)
│   ├── Repositories/    (OrderRepository : IOrderRepository)
│   └── Migrations/
├── Messaging/           (RabbitMQ event bus wiring, integration event handlers)
└── DependencyInjection.cs

Ordering.Api/
├── Controllers/         (thin — dispatch command/query, return ProblemDetails on failure)
├── Extensions/
├── appsettings.json
└── Program.cs
```

## Project Reference Graph

Set these up exactly. The `ArchitectureTests` project will fail the build if they drift.

- `*.Domain` → `BuildingBlocks.Domain` only.
- `*.Application` → its own `*.Domain`, `BuildingBlocks.Cqrs`, `BuildingBlocks.Messaging` (abstractions).
- `*.Infrastructure` → its own `*.Application`, `*.Domain`, `BuildingBlocks.Persistence`, `BuildingBlocks.Messaging`.
- `*.Api` → its own `*.Application`, `*.Infrastructure`, `BuildingBlocks.Web`, `ServiceDefaults`.
- `Gateway.Yarp` → `ServiceDefaults`, `BuildingBlocks.Web` (no business projects).
- `AppHost` → references each `*.Api` project and `Gateway.Yarp` as Aspire resources.

> **Why Infrastructure-references-Application-not-the-reverse matters:** the Application layer declares
> `IOrderRepository`; Infrastructure provides `OrderRepository`. This is what lets you unit-test handlers
> with fakes and swap PostgreSQL for an in-memory store in tests without touching business logic.

## Build Order (recommended generation sequence)

When scaffolding from scratch, generate in this order so each layer compiles against what already exists:

1. `BuildingBlocks.Domain` + `BuildingBlocks.Cqrs` → see `references/cqrs-framework.md`
2. `BuildingBlocks.Persistence` (DbContext base, `IUnitOfWork`, audit interceptor) → `references/data-efcore.md`
3. `BuildingBlocks.Messaging` (RabbitMQ event bus) → `references/messaging-rabbitmq.md`
4. `BuildingBlocks.Web` (exception middleware, correlation ID) → `references/cross-cutting.md`
5. One vertical slice of one service (Domain → Application → Infrastructure → Api) end-to-end
6. `Identity` service with JWT issuance + refresh tokens → `references/security-jwt.md`
7. `Gateway.Yarp` → `references/yarp-gateway.md`
8. `Aspire/ServiceDefaults` + `Aspire/AppHost` → `references/aspire-apphost.md`
9. `ArchitectureTests` to lock the dependency graph

## Reference Files — read before generating that concern

| Concern | File | Read it when… |
|---|---|---|
| Custom CQRS (no MediatR), dispatchers, pipeline behaviors, validation | `references/cqrs-framework.md` | building any command/query/handler or the dispatch pipeline |
| EF Core 10, database-first, migrations, Repository + UoW, shared DB lib | `references/data-efcore.md` | touching persistence, the shared DbContext, or scaffolding |
| RabbitMQ event bus, integration events, handlers, retry/outbox | `references/messaging-rabbitmq.md` | any cross-service messaging |
| YARP routes, clusters, JWT at the edge, rate limiting, Swagger aggregation | `references/yarp-gateway.md` | the gateway |
| .NET Aspire AppHost, ServiceDefaults, discovery, health, OTel wiring | `references/aspire-apphost.md` | orchestration / observability bootstrap |
| Global exception handling, Serilog, correlation ID, audit, ProblemDetails | `references/cross-cutting.md` | middleware / logging / diagnostics |
| JWT issuance, refresh tokens, role-based authz, gateway token validation | `references/security-jwt.md` | anything auth |

## Output Conventions

- Generate real, compiling C# — not pseudocode. Use file-scoped namespaces, primary constructors, and
  `required` members where they read cleanly. Target `net10.0`, `LangVersion` latest.
- Lead with the solution/folder structure, then generate per the build order above, explaining the *why*
  before each non-obvious block (the user is a senior engineer — skip beginner explanations, justify
  design trade-offs instead).
- For large multi-file output, write actual files to disk and present them, rather than dumping 3,000
  lines into chat.
- Keep controllers thin: validate input shape, dispatch, map result to HTTP. No business logic in the API
  layer, ever.
