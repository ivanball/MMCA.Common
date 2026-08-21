namespace MMCA.Common.Testing.Tests;

/// <summary>
/// Exercises <see cref="MiddlewarePipelineOrderTestsBase"/> against the framework's own default edge
/// pipeline, the one every service host gets from the zero-argument
/// <c>UseCommonMiddlewarePipeline()</c> overload. No host here customizes the pipeline, so nothing
/// is overridden: this is the Common-side conformance instance of the fitness function the
/// downstream repos subclass the same way.
/// </summary>
public sealed class MiddlewarePipelineOrderTests : MiddlewarePipelineOrderTestsBase;
