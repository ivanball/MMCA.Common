using System.Reflection;

namespace MMCA.Common.Architecture.Tests.Helpers;

/// <summary>
/// Provides assembly references for each MMCA.Common package layer.
/// Used by architecture tests to verify dependency rules.
/// </summary>
internal static class PackageAssemblies
{
    internal static Assembly Shared =>
        typeof(Common.Shared.Abstractions.Result).Assembly;

    internal static Assembly Domain =>
        typeof(Common.Domain.Entities.BaseEntity<>).Assembly;

    internal static Assembly Application =>
        typeof(Common.Application.Services.DomainEventDispatcher).Assembly;

    internal static Assembly Infrastructure =>
        typeof(Common.Infrastructure.Persistence.DbContexts.ApplicationDbContext).Assembly;

    internal static Assembly Api =>
        typeof(Common.API.Controllers.ApiControllerBase).Assembly;

    internal static Assembly Grpc =>
        typeof(Common.Grpc.ResultGrpcExtensions).Assembly;

    internal static Assembly UI =>
        typeof(Common.UI.UISharedAssemblyReference).Assembly;
}
