# EF Core 10 — Database-First, Shared DB Library, UoW & Repository

Persistence lives partly in the shared `BuildingBlocks.Persistence` (base `DbContext`, `IUnitOfWork`,
audit interceptor, domain-event dispatch) and partly per-service (`OrderingDbContext`, configurations,
repositories). PostgreSQL 18 via Npgsql. **Database-first**: the schema is authoritative.

## Contents
- [Database-first workflow](#database-first-workflow)
- [Shared building blocks](#shared-building-blocks)
- [Unit of Work + domain events](#unit-of-work--domain-events)
- [Audit interceptor](#audit-interceptor)
- [Repository — only where it earns its place](#repository--only-where-it-earns-its-place)
- [Entity configuration](#entity-configuration)
- [DI wiring](#di-wiring)

## Database-first workflow

The schema exists first (managed as canonical SQL). Scaffold read/write entities from it; treat
EF migrations as drift-tracking, not the source of truth.

```bash
# Scaffold from an existing PostgreSQL 18 database into Infrastructure.
# --no-onconfiguring keeps connection strings out of the context (use DI).
# Split entities and context; place entities in Domain-adjacent folder, context in Infrastructure.
dotnet ef dbcontext scaffold \
  "Host=localhost;Port=5432;Database=ordering;Username=app;Password=***" \
  Npgsql.EntityFrameworkCore.PostgreSQL \
  --context OrderingDbContext \
  --context-dir Persistence \
  --output-dir Persistence/Scaffolded \
  --schema ordering \
  --no-onconfiguring \
  --use-database-names \
  --data-annotations
```

After the first scaffold, **stop regenerating wholesale.** Hand-author `IEntityTypeConfiguration<T>`
classes and let scaffolding inform them. To detect drift between code and DB, generate a migration and
inspect it — if it's non-empty, the model and schema disagree:

```bash
dotnet ef migrations add DriftCheck --output-dir Persistence/Migrations
# Inspect the Up() body. Empty = in sync. Non-empty = reconcile, then remove the migration.
dotnet ef migrations remove   # if it was only a drift probe
```

For deliberate, code-led changes layered on top of the canonical schema, keep migrations and apply with
`dotnet ef database update` (or an idempotent script `dotnet ef migrations script --idempotent` run by
the deploy pipeline / Aspire).

## Shared building blocks

```csharp
// BuildingBlocks.Domain/Entity.cs
namespace BuildingBlocks.Domain;

public abstract class Entity
{
    public Guid Id { get; protected set; } = Guid.CreateVersion7(); // time-ordered, index-friendly
}

public abstract class AggregateRoot : Entity
{
    private readonly List<IDomainEvent> _domainEvents = [];
    public IReadOnlyCollection<IDomainEvent> DomainEvents => _domainEvents.AsReadOnly();
    protected void Raise(IDomainEvent @event) => _domainEvents.Add(@event);
    public void ClearDomainEvents() => _domainEvents.Clear();
}

public interface IDomainEvent { DateTime OccurredOnUtc => DateTime.UtcNow; }

// Audit contract implemented by entities that should be stamped automatically.
public interface IAuditable
{
    DateTime CreatedAtUtc { get; set; }
    string? CreatedBy { get; set; }
    DateTime? ModifiedAtUtc { get; set; }
    string? ModifiedBy { get; set; }
}
```

```csharp
// BuildingBlocks.Persistence/Abstractions/IUnitOfWork.cs
namespace BuildingBlocks.Persistence;

public interface IUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken ct = default);
}
```

## Unit of Work + domain events

The base context implements `IUnitOfWork`. On save, it dispatches domain events *before* committing so
handlers participate in the same transaction, then persists.

```csharp
// BuildingBlocks.Persistence/BaseDbContext.cs
using BuildingBlocks.Domain;
using Microsoft.EntityFrameworkCore;

namespace BuildingBlocks.Persistence;

public abstract class BaseDbContext(DbContextOptions options, IDomainEventDispatcher dispatcher)
    : DbContext(options), IUnitOfWork
{
    public override async Task<int> SaveChangesAsync(CancellationToken ct = default)
    {
        // Collect domain events from tracked aggregates before saving.
        var aggregates = ChangeTracker.Entries<AggregateRoot>()
            .Where(e => e.Entity.DomainEvents.Count > 0)
            .Select(e => e.Entity)
            .ToArray();

        var events = aggregates.SelectMany(a => a.DomainEvents).ToArray();
        foreach (var a in aggregates) a.ClearDomainEvents();

        // Dispatch in-process domain handlers (NOT RabbitMQ — that's integration events).
        await dispatcher.DispatchAsync(events, ct);

        return await base.SaveChangesAsync(ct);
    }
}
```

> **Domain events vs integration events:** domain events stay in-process and run inside this transaction
> (e.g. "recalculate order total"). When a domain handler needs to tell *another service*, it enqueues an
> **integration event** to the outbox — see `references/messaging-rabbitmq.md`. Never publish to RabbitMQ
> from inside `SaveChangesAsync`; that couples your transaction to the broker.

## Audit interceptor

Stamp `IAuditable` automatically using a `SaveChangesInterceptor` and the current user from
`ICurrentUser` (populated from the JWT — see `references/security-jwt.md`).

```csharp
// BuildingBlocks.Persistence/Interceptors/AuditInterceptor.cs
using BuildingBlocks.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace BuildingBlocks.Persistence;

public sealed class AuditInterceptor(ICurrentUser currentUser) : SaveChangesInterceptor
{
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
    {
        var context = eventData.Context;
        if (context is null) return base.SavingChangesAsync(eventData, result, ct);

        var now = DateTime.UtcNow;
        var user = currentUser.UserId?.ToString();

        foreach (var entry in context.ChangeTracker.Entries<IAuditable>())
        {
            if (entry.State == EntityState.Added)
            {
                entry.Entity.CreatedAtUtc = now;
                entry.Entity.CreatedBy = user;
            }
            else if (entry.State == EntityState.Modified)
            {
                entry.Entity.ModifiedAtUtc = now;
                entry.Entity.ModifiedBy = user;
            }
        }
        return base.SavingChangesAsync(eventData, result, ct);
    }
}
```

## Repository — only where it earns its place

Don't create a generic `IRepository<T>` for everything; it just hides `DbSet` behind worse ergonomics.
Use a repository when an aggregate has real invariants and non-trivial loading (include graphs, spec-style
queries). Let plain read queries project straight off the context.

```csharp
// Ordering.Application/Abstractions/IOrderRepository.cs  (interface in Application)
public interface IOrderRepository
{
    Task<Order?> GetByIdAsync(Guid id, CancellationToken ct);
    Task AddAsync(Order order, CancellationToken ct);
    void Remove(Order order);
}
```

```csharp
// Ordering.Infrastructure/Persistence/Repositories/OrderRepository.cs  (impl in Infrastructure)
using Microsoft.EntityFrameworkCore;

public sealed class OrderRepository(OrderingDbContext db) : IOrderRepository
{
    public Task<Order?> GetByIdAsync(Guid id, CancellationToken ct) =>
        db.Orders
          .Include(o => o.Items)          // load the full aggregate for write consistency
          .FirstOrDefaultAsync(o => o.Id == id, ct);

    public async Task AddAsync(Order order, CancellationToken ct) =>
        await db.Orders.AddAsync(order, ct);

    public void Remove(Order order) => db.Orders.Remove(order);
}
```

**Read side bypasses the repository.** A query handler projects directly with `AsNoTracking`:

```csharp
// Ordering.Application/Queries/GetOrderById/GetOrderByIdQueryHandler.cs
public sealed class GetOrderByIdQueryHandler(OrderingDbContext db)  // read model can touch context
    : IQueryHandler<GetOrderByIdQuery, OrderDto>
{
    public Task<OrderDto> Handle(GetOrderByIdQuery query, CancellationToken ct) =>
        db.Orders.AsNoTracking()
          .Where(o => o.Id == query.OrderId)
          .Select(o => new OrderDto(o.Id, o.CustomerId, o.Total,
              o.Items.Select(i => new OrderLineDto(i.Sku, i.Quantity)).ToList()))
          .FirstOrDefaultAsync(ct)!
        ?? throw new OrderNotFoundException(query.OrderId);
}
```

> Exposing `OrderingDbContext` to the read-side query handler is a deliberate CQRS trade-off: writes go
> through the aggregate + repository to protect invariants; reads optimize for shape and speed. If you
> want stricter isolation, define an `IOrderingReadDbContext` interface exposing `IQueryable` `DbSet`s.

## Entity configuration

Keep mapping in `IEntityTypeConfiguration<T>` files, not in `OnModelCreating`. Map the schema explicitly
so it matches the canonical PostgreSQL definitions.

```csharp
// Ordering.Infrastructure/Persistence/Configurations/OrderConfiguration.cs
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public sealed class OrderConfiguration : IEntityTypeConfiguration<Order>
{
    public void Configure(EntityTypeBuilder<Order> builder)
    {
        builder.ToTable("orders", "ordering");
        builder.HasKey(o => o.Id);
        builder.Property(o => o.CustomerId).HasColumnName("customer_id").IsRequired();

        builder.OwnsMany(o => o.Items, items =>
        {
            items.ToTable("order_items", "ordering");
            items.WithOwner().HasForeignKey("order_id");
            items.Property<long>("id"); // shadow PK
            items.HasKey("id");
        });

        builder.Property(o => o.CreatedAtUtc).HasColumnName("created_at_utc");
        builder.Property(o => o.CreatedBy).HasColumnName("created_by");
    }
}
```

```csharp
// Ordering.Infrastructure/Persistence/OrderingDbContext.cs
using System.Reflection;
using BuildingBlocks.Persistence;
using Microsoft.EntityFrameworkCore;

public sealed class OrderingDbContext(
    DbContextOptions<OrderingDbContext> options, IDomainEventDispatcher dispatcher)
    : BaseDbContext(options, dispatcher)
{
    public DbSet<Order> Orders => Set<Order>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("ordering");
        modelBuilder.ApplyConfigurationsFromAssembly(Assembly.GetExecutingAssembly());
        base.OnModelCreating(modelBuilder);
    }
}
```

## DI wiring

```csharp
// Ordering.Infrastructure/DependencyInjection.cs
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;

public static class InfrastructureRegistration
{
    public static IServiceCollection AddOrderingInfrastructure(
        this IServiceCollection services, IConfiguration config)
    {
        services.AddScoped<AuditInterceptor>();

        services.AddDbContext<OrderingDbContext>((sp, options) =>
        {
            options.UseNpgsql(
                config.GetConnectionString("ordering-db"),     // Aspire injects this
                npgsql => npgsql.MigrationsHistoryTable("__migrations", "ordering"));
            options.AddInterceptors(sp.GetRequiredService<AuditInterceptor>());
        });

        // BaseDbContext is the IUnitOfWork for this service.
        services.AddScoped<IUnitOfWork>(sp => sp.GetRequiredService<OrderingDbContext>());
        services.AddScoped<IOrderRepository, OrderRepository>();
        return services;
    }
}
```

> The connection string name `"ordering-db"` is the Aspire resource name — Aspire wires the real
> connection string at runtime via service discovery. See `references/aspire-apphost.md`.
