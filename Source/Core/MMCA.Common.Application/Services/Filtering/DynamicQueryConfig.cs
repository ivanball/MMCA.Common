using System.Linq.Dynamic.Core;

namespace MMCA.Common.Application.Services.Filtering;

/// <summary>
/// Shared <see cref="ParsingConfig"/> for every System.Linq.Dynamic.Core call in the query pipeline
/// (the filter strategies and <c>QueryFieldService.ApplySorting</c>).
/// <para>
/// Dynamic LINQ defaults <see cref="ParsingConfig.UseParameterizedNamesInDynamicQuery"/> to
/// <see langword="false"/>, which turns each <c>@0</c> argument into a <c>ConstantExpression</c>.
/// EF inlines constants, so <c>filters["Name"] = ("EQUALS", "Widget")</c> emitted
/// <c>WHERE [Name] = 'Widget'</c>: a distinct SQL string per distinct filter value, which costs a
/// SQL Server plan-cache entry per value and misses EF's compiled-query cache on every request.
/// With the flag on, the value is reached through a member access instead, so EF parameterizes it
/// and one plan serves every value. <c>QueryParameterizationTests</c> is the guard.
/// </para>
/// </summary>
internal static class DynamicQueryConfig
{
    /// <summary>The single instance every dynamic-LINQ call site passes; building one per call would reintroduce the parse cost this avoids.</summary>
    internal static readonly ParsingConfig Parameterized = new()
    {
        UseParameterizedNamesInDynamicQuery = true,
    };
}
