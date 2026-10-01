// Load scenarios measure wall-clock throughput and latency, so they must not compete with each
// other for the CPU or the disk: every test runs sequentially.
[assembly: Xunit.v3.Parallelization(Mode = Xunit.Sdk.ParallelMode.None)]
