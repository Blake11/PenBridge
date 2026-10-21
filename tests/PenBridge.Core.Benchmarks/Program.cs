using BenchmarkDotNet.Running;

// Manual only (not part of scripts/test.sh or CI):
//   dotnet run -c Release --project tests/PenBridge.Core.Benchmarks -- --filter '*' [--job short]
BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
