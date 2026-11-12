namespace MMCA.Common.API.Pagination;

/// <summary>
/// Opts an entity controller in to the change-feed action,
/// <c>GET {controller}/changes?since=&amp;cursor=&amp;pageSize=</c>, served by
/// <c>EntityControllerBase.GetChangesAsync</c>: the rows changed at or after <c>since</c>, soft-deleted
/// ones as tombstones. A controller without it has no such endpoint and an unchanged API document.
/// </summary>
/// <remarks>
/// Declared on an abstract base controller it opts in every controller derived from it. The query
/// service's <c>GetChangesSinceAsync</c> serves the page; a service that predates it answers 500.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class SupportsChangeFeedAttribute : Attribute;
