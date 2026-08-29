// Stand-in command and query types whose namespace tail mirrors the workspace
// MMCA.{App}.{Module}.{Layer} convention (module "Billing", layer "Domain"), so the logging
// decorators' module-name enrichment can be exercised without a project reference on a real
// module. The folder nesting deliberately matches the namespace so IDE0130 stays satisfied.

namespace MMCA.Common.Application.Tests.Fakes.Billing.Domain;

// Must be public for Moq DynamicProxy, like the other decorator test types.
public sealed record BillingFakeCommand;

public sealed record BillingFakeQuery;
