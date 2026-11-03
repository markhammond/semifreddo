// Semifreddo against other persistent collections for .NET (BenchmarkDotNet). Examples:
//   dotnet run -c Release -- --filter '*'                        every benchmark, the default job
//   dotnet run -c Release -- --filter '*Vector*' --job short     vectors only, a quicker (noisier) job
//   SEMIFREDDO_BENCH_N=100000 dotnet run -c Release -- --filter '*' --launchCount 1 --warmupCount 3 --iterationCount 10
//                                                                the README's tables: N = 100,000, fixed iterations
//   dotnet run -c Release -- --list flat                          what there is
using BenchmarkDotNet.Running;

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
