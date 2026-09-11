namespace MMCA.Common.Testing.Architecture;

/// <summary>
/// Fitness functions over a finalized EF Core model: nothing cascades unless somebody said so, and
/// everything else restricts.
/// <para>
/// The framework's <c>RestrictDeleteByDefaultConvention</c> sets
/// <c>DeleteBehavior.Restrict</c> on every relationship no entity configuration configured, and stamps
/// each foreign key with <c>MMCA:DeleteBehaviorSource</c> (<c>Explicit</c>, <c>Convention</c> or
/// <c>Ownership</c>). These two rules read that stamp back off the finished model, so a repo learns at
/// test time that its schema deletes the way its configurations say it does.
/// </para>
/// <para>
/// <b>Subclassing.</b> Supply <see cref="Model"/> as the <c>Model</c> property of a context built the
/// way the app builds it (any relational engine: the model is what is asserted, not the database, so
/// an in-memory SQLite context is enough and no server is needed). Point it at a <b>relational</b>
/// model: the convention is a deliberate no-op for Cosmos DB, which has no foreign key constraints to
/// restrict, so a Cosmos model carries no stamps to assert.
/// </para>
/// </summary>
public abstract class DeleteBehaviorConventionTestsBase
{
    /// <summary>
    /// The finalized EF Core model to assert, typed as <see cref="object"/> because this package
    /// deliberately takes no EF Core reference (see the csproj comment). Pass <c>dbContext.Model</c>.
    /// </summary>
    protected abstract object Model { get; }

    [Fact]
    public void CascadingDeletes_AreExplicitlyOptedIn() => ArchitectureRules.CascadingForeignKeysAreExplicitlyOptedIn(Model);

    [Fact]
    public void ConventionDeletes_AreRestricted() => ArchitectureRules.ConventionForeignKeysRestrictDeletes(Model);
}
