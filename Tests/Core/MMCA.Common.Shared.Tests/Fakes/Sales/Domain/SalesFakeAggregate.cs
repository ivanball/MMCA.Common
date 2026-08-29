// Stand-in types whose namespace tail mirrors the workspace MMCA.{App}.{Module}.{Layer}
// convention (module "Sales", layer "Domain"), so ModuleNameConventions can be exercised on a
// conventionally-named namespace without referencing a layer above Shared. The folder nesting
// deliberately matches the namespace so IDE0130 stays satisfied.

namespace MMCA.Common.Shared.Tests.Fakes.Sales.Domain;

// Domain is the LAST namespace segment here.
public sealed class SalesFakeAggregate;
