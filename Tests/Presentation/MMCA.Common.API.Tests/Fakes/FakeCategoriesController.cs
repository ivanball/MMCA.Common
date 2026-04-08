// Stand-in type whose namespace mirrors the real MMCA.Store.Catalog.API.Controllers
// convention so ModuleControllerFeatureProvider's namespace-token matching can be
// exercised in unit tests without taking a project reference on a real module.

namespace Fakes.MMCA.Store.Catalog.API.Controllers;

public sealed class FakeCategoriesController
{
}
