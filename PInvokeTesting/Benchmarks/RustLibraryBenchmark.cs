using System;
using BenchmarkDotNet.Attributes;

namespace PInvokeTesting.Benchmarks
{
    public class RustLibraryBenchmark
    {
        private RustView view;

        public RustLibraryBenchmark()
        {

        }

        [Benchmark]
        public void SendNativeViewCommand()
        {
             view = new RustView();
        }

        [IterationCleanup]
        public void Close()
        {
            view.Dispose();
        }
    }
}
