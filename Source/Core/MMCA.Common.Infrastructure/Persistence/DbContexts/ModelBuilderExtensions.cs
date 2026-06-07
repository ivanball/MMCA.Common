using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MMCA.Common.Infrastructure.Persistence.DbContexts;

/// <summary>
/// Extension methods for <see cref="ModelBuilder"/> to support convention-based EF configuration discovery.
/// </summary>
internal static class ModelBuilderExtensions
{
    /// <summary>
    /// Scans the given assembly for concrete classes implementing <paramref name="interfaceType"/>
    /// (a provider-specific configuration interface like <c>IEntityTypeConfigurationSQLServer&lt;,&gt;</c>),
    /// instantiates each via DI, and applies it to the model builder. An optional
    /// <paramref name="entityFilter"/> restricts application to entities belonging to the
    /// context's physical data source.
    /// </summary>
    /// <param name="modelBuilder">The model builder to apply configurations to.</param>
    /// <param name="serviceProvider">Service provider for resolving configuration constructor dependencies.</param>
    /// <param name="assembly">The assembly to scan for configuration classes.</param>
    /// <param name="interfaceType">The open generic interface type to match (e.g., <c>IEntityTypeConfigurationSQLServer&lt;,&gt;</c>).</param>
    /// <param name="entityFilter">Optional predicate over the entity CLR type; configurations whose entity fails the predicate are skipped.</param>
    internal static void ApplyAllConfigurations(
        this ModelBuilder modelBuilder,
        IServiceProvider serviceProvider,
        Assembly assembly,
        Type interfaceType,
        Func<Type, bool>? entityFilter = null)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentNullException.ThrowIfNull(interfaceType);

        // Resolve the open generic ApplyConfiguration<TEntity>(IEntityTypeConfiguration<TEntity>) method
        // so we can close it per entity type below.
        var applyConfigMethod = typeof(ModelBuilder)
            .GetMethods()
            .First(m => m.Name == nameof(ModelBuilder.ApplyConfiguration) && m.GetParameters().Length == 1);

        var configTypes = assembly
            .GetTypes()
            .Where(t => !t.IsAbstract && !t.IsGenericTypeDefinition)
            .Select(t => new
            {
                Type = t,
                Interface = t.GetInterfaces()
                    .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == interfaceType)
            })
            .Where(x => x.Interface is not null);

        foreach (var config in configTypes)
        {
            // First generic argument is the entity type (TEntity).
            var entityType = config.Interface!.GenericTypeArguments[0];
            if (entityFilter is not null && !entityFilter(entityType))
            {
                continue;
            }

            var configInstance = ActivatorUtilities.CreateInstance(serviceProvider, config.Type);
            var genericMethod = applyConfigMethod.MakeGenericMethod(entityType);
            genericMethod.Invoke(modelBuilder, [configInstance]);
        }
    }
}
