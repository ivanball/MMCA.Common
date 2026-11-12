namespace MMCA.Common.API.Pagination;

/// <summary>
/// Opts an entity controller in to the cursor (keyset) list action,
/// <c>GET {controller}/cursor?sortColumn=&amp;sortDirection=&amp;cursor=&amp;pageSize=</c>, served by
/// <c>EntityControllerBase.GetCursorPageAsync</c>. A controller without it has no such endpoint and an
/// unchanged API document.
/// </summary>
/// <remarks>
/// Declared on an abstract base controller it opts in every controller derived from it. The query
/// service's <c>GetPageByCursorAsync</c> serves the page; a service that predates it answers 500.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class SupportsCursorPagingAttribute : Attribute;
