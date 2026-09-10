using Microsoft.EntityFrameworkCore;
using MMCA.Common.Infrastructure.Persistence.Conversions;
using MMCA.Common.Shared.Identifiers;

namespace MMCA.Common.Infrastructure.Persistence.Conventions;

/// <summary>
/// Declares one pre-convention type mapping per strongly typed identifier a host has opted into, so
/// EVERY property of that type maps to the wrapped primitive: keys, cross-module scalar references,
/// owned-type members, and the properties of a framework table alike. Called once from
/// <c>ApplicationDbContext.ConfigureConventions</c>, which every engine context inherits, so SQL
/// Server, PostgreSQL, SQLite and Cosmos all get the same mapping without an engine branch.
/// <para>
/// Pre-convention rather than a model-finalizing convention on purpose. A converter attached at
/// finalization arrives after the provider's value-generation conventions have already inspected the
/// property, so a wrapped <see langword="int"/> key would silently lose its IDENTITY strategy. Declared here,
/// the provider CLR type is known before any of that runs and a wrapped key generates exactly as its
/// primitive did.
/// </para>
/// </summary>
public static class StronglyTypedIdModelConfiguration
{
    /// <summary>
    /// Applies the conversion and comparer for every identifier in <paramref name="registry"/>.
    /// </summary>
    /// <param name="configurationBuilder">The pre-convention model configuration builder.</param>
    /// <param name="registry">The identifier types this host declares.</param>
    /// <returns>The number of identifier types configured.</returns>
    public static int Apply(ModelConfigurationBuilder configurationBuilder, StronglyTypedIdRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);
        ArgumentNullException.ThrowIfNull(registry);

        var configured = 0;

        foreach (var (identifierType, valueType) in registry.Describe())
        {
            var converterType = typeof(StronglyTypedIdValueConverter<,>)
                .MakeGenericType(identifierType, valueType);
            var comparerType = typeof(StronglyTypedIdValueComparer<>)
                .MakeGenericType(identifierType);

            configurationBuilder
                .Properties(identifierType)
                .HaveConversion(converterType, comparerType);

            configured++;
        }

        return configured;
    }
}
