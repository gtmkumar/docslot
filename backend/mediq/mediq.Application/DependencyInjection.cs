using System.Reflection;
using FluentValidation;
using mediq.Application.Cqrs;
using Microsoft.Extensions.DependencyInjection;

namespace mediq.Application;

public static class ApplicationRegistration
{
    /// <summary>
    /// Registers the custom CQRS framework: dispatchers, every handler in this assembly (scanned once),
    /// all FluentValidation validators, and the behavior pipelines.
    /// Command order: Logging → Validation → Idempotency → UnitOfWork → handler.
    /// Query order:  Logging → handler (no UoW/audit/idempotency on reads).
    /// </summary>
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        var assembly = typeof(ApplicationRegistration).Assembly;

        services.AddScoped<ICommandDispatcher, CommandDispatcher>();
        services.AddScoped<IQueryDispatcher, QueryDispatcher>();

        RegisterClosedImplementations(services, assembly, typeof(ICommandHandler<,>));
        RegisterClosedImplementations(services, assembly, typeof(IQueryHandler<,>));

        services.AddValidatorsFromAssembly(assembly, includeInternalTypes: true);

        // Command pipeline (outer → inner).
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(LoggingBehavior<,>));
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(IdempotencyBehavior<,>));
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(UnitOfWorkBehavior<,>));
        // Innermost (closest to the handler): translate Domain rule violations → 422 before UoW commits.
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(DomainExceptionTranslationBehavior<,>));

        // Query pipeline: tenant-scope (RLS on reads) → logging.
        services.AddScoped(typeof(IQueryPipelineBehavior<,>), typeof(TenantScopeQueryBehavior<,>));
        services.AddScoped(typeof(IQueryPipelineBehavior<,>), typeof(LoggingBehavior<,>));

        return services;
    }

    private static void RegisterClosedImplementations(
        IServiceCollection services, Assembly assembly, Type openInterface)
    {
        var registrations = assembly.GetTypes()
            .Where(t => t is { IsAbstract: false, IsInterface: false })
            .SelectMany(t => t.GetInterfaces()
                .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == openInterface)
                .Select(i => (Service: i, Implementation: t)));

        foreach (var (service, implementation) in registrations)
            services.AddScoped(service, implementation);
    }
}
