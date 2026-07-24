using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Query;
using MMCA.Common.Application.Interfaces.Infrastructure;

namespace MMCA.Common.Infrastructure.Persistence.Repositories;

/// <summary>
/// Collects the property assignments described through the persistence-agnostic
/// <see cref="IUpdatePropertySetter{TEntity}"/> surface and replays them onto EF Core 10's
/// <see cref="UpdateSettersBuilder{TSource}"/> when <c>ExecuteUpdateAsync</c> runs. This is
/// the bridge that keeps EF Core out of the Application layer.
/// </summary>
/// <typeparam name="TEntity">The entity type being updated.</typeparam>
internal sealed class UpdatePropertySetterBuilder<TEntity> : IUpdatePropertySetter<TEntity>
{
    private readonly List<Action<UpdateSettersBuilder<TEntity>>> _assignments = [];
    private readonly HashSet<string> _assignedProperties = [];

    /// <inheritdoc />
    public IUpdatePropertySetter<TEntity> Set<TProperty>(
        Expression<Func<TEntity, TProperty>> property,
        TProperty value)
    {
        ArgumentNullException.ThrowIfNull(property);
        TrackPropertyName(property);
        _assignments.Add(builder => builder.SetProperty(property, value));
        return this;
    }

    /// <inheritdoc />
    public IUpdatePropertySetter<TEntity> Set<TProperty>(
        Expression<Func<TEntity, TProperty>> property,
        Expression<Func<TEntity, TProperty>> valueFactory)
    {
        ArgumentNullException.ThrowIfNull(property);
        ArgumentNullException.ThrowIfNull(valueFactory);
        TrackPropertyName(property);
        _assignments.Add(builder => builder.SetProperty(property, valueFactory));
        return this;
    }

    /// <summary>Gets a value indicating whether any assignment was described.</summary>
    public bool IsEmpty => _assignments.Count == 0;

    /// <summary>
    /// Returns <see langword="true"/> when the caller already assigned the named top-level
    /// property, so automatic audit stamping does not overwrite an explicit value.
    /// </summary>
    public bool SetsProperty(string propertyName) => _assignedProperties.Contains(propertyName);

    /// <summary>Replays every collected assignment onto EF Core's setters builder.</summary>
    public void Apply(UpdateSettersBuilder<TEntity> builder)
    {
        foreach (var assignment in _assignments)
        {
            assignment(builder);
        }
    }

    private void TrackPropertyName(LambdaExpression property)
    {
        if (property.Body is MemberExpression member)
        {
            _assignedProperties.Add(member.Member.Name);
        }
    }
}
