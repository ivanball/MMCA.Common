using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Dotnet;
using AwesomeAssertions;
using MMCA.Common.Testing.Aspire.Fixtures;

namespace MMCA.Common.Testing.Aspire.Tests.Fixtures;

/// <summary>
/// Which resources the fixture waits for. The default (no names) must find the AddDotnetProject
/// resources and must never pass vacuously: a model with no project resource is a failure, because a
/// fixture that awaits nothing hands every test an application whose services never started.
/// </summary>
public sealed class AppHostResourceSelectionTests
{
    [Fact]
    public void NoNames_SelectsEveryDotnetProjectResource_AndSkipsContainers()
    {
        var model = new DistributedApplicationModel(
        [
            new DotnetProjectResource("identity", "."),
            new ContainerResource("redis"),
            new DotnetProjectResource("gateway", "."),
        ]);

        var selected = AppHostFixtureBase.SelectResources(model, names: null);

        selected.Select(resource => resource.Name).Should().Equal("identity", "gateway");
    }

    [Fact]
    public void NoNames_WithNoProjectResource_Throws()
    {
        var model = new DistributedApplicationModel([new ContainerResource("redis")]);

        var act = () => AppHostFixtureBase.SelectResources(model, names: null);

        act.Should().Throw<InvalidOperationException>().WithMessage("*no project resource*");
    }

    [Fact]
    public void Names_SelectTheNamedResources_InTheGivenOrder()
    {
        var model = new DistributedApplicationModel(
        [
            new DotnetProjectResource("identity", "."),
            new ContainerResource("redis"),
        ]);

        var selected = AppHostFixtureBase.SelectResources(model, ["redis", "identity"]);

        selected.Select(resource => resource.Name).Should().Equal("redis", "identity");
    }

    [Fact]
    public void Names_WithAnUnknownName_Throws()
    {
        var model = new DistributedApplicationModel([new DotnetProjectResource("identity", ".")]);

        var act = () => AppHostFixtureBase.SelectResources(model, ["missing"]);

        act.Should().Throw<InvalidOperationException>().WithMessage("*'missing'*");
    }
}
