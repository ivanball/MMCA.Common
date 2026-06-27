using BenchmarkDotNet.Running;

// Entry point for the benchmark smoke harness. Pass a `--filter` to select benchmarks, or `--job Dry`
// for a fast correctness smoke. See the .csproj header for run commands.
BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
