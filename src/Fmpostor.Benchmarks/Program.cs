using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Running;
using Fmpostor.Benchmarks.Tests;

namespace Fmpostor.Benchmarks
{
    internal static class Program
    {
        private static void Main(string[] args)
        {
            // BenchmarkRunner.Run<EventManagerBenchmark>(
            //     DefaultConfig.Instance
            //         .AddDiagnoser(MemoryDiagnoser.Default)
            // );

            BenchmarkRunner.Run<MessageReaderBenchmark>(
                DefaultConfig.Instance
                    .AddDiagnoser(MemoryDiagnoser.Default)
            );
        }
    }
}
