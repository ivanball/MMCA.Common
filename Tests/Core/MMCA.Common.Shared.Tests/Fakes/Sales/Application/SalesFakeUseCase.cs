// Stand-in type whose namespace tail mirrors the workspace MMCA.{App}.{Module}.{Layer}
// convention with a non-Domain layer (module "Sales", layer "Application"), so the generalized
// layer-segment parse in ModuleNameConventions can be exercised. The folder nesting deliberately
// matches the namespace so IDE0130 stays satisfied.

namespace MMCA.Common.Shared.Tests.Fakes.Sales.Application;

// Application is the layer segment here; it sits at index 6, past the minimum index the parser
// requires for non-Domain layers.
public sealed class SalesFakeUseCase;
