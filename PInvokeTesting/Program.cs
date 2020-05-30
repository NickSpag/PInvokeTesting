using System;
using BenchmarkDotNet.Running;
using PInvokeTesting.Benchmarks;

namespace PInvokeTesting
{
    class Program
    {
        static void Main(string[] args)
        {
            BenchmarkRunner.Run<JsonViaStdInOutBenchmark>();
            BenchmarkRunner.Run<RustLibraryBenchmark>();

            //var bench = new JsonViaStdInOutBenchmark();
            //bench.SendSerializedObject();

            //try
            //{
            //    NativeBinding.CreateView();
            //    NativeBinding.Edit("coooome on baby");
            //}
            //catch (Exception ex)
            //{
            //    System.Console.WriteLine();
            //}

        }
    }
}
