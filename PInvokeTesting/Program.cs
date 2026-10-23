using System;
using System.Text;
using BenchmarkDotNet.Running;
using PInvokeTesting.Benchmarks;

namespace PInvokeTesting
{
    class Program
    {
        static int Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "bench")
            {
                BenchmarkRunner.Run<JsonViaStdInOutBenchmark>();
                BenchmarkRunner.Run<RustLibraryBenchmark>();
                return 0;
            }

            var failures = 0;
            void Check(bool ok, string what)
            {
                Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {what}");
                if (!ok) failures++;
            }

            using (var view = new RustView())
            {
                Check(view.Text == "hey dotnet!", $"initial text reads back: \"{view.Text}\"");

                string lastUpdate = null;
                view.Updated += text => lastUpdate = text;

                view.Insert(" — héllo, 🦀");
                Check(view.Text == "hey dotnet! — héllo, 🦀", $"non-ASCII insert round-trips: \"{view.Text}\"");
                Check(lastUpdate == view.Text, "Rust called back into C# with the new text");

                view.Insert("a\0b");
                Check(view.Text.EndsWith("a\0b"), "embedded NUL survives (length-prefixed, not C string)");

                try
                {
                    view.Insert(new byte[] { 0xFF, 0xFE });
                    Check(false, "invalid UTF-8 rejected");
                }
                catch (ArgumentException)
                {
                    Check(true, "invalid UTF-8 rejected with a status code, no panic across FFI");
                }
            }
            Check(RustBindings.LiveViews == 0, $"Dispose freed the view (live: {RustBindings.LiveViews})");

            // Never disposed: the SafeHandle's finalizer has to free them.
            LeakViews(1000);
            Check(RustBindings.LiveViews == 1000, $"1000 undisposed views alive (live: {RustBindings.LiveViews})");
            GC.Collect();
            GC.WaitForPendingFinalizers();
            Check(RustBindings.LiveViews == 0, $"finalizer freed every undisposed view (live: {RustBindings.LiveViews})");

            return failures == 0 ? 0 : 1;
        }

        // Separate method so no local keeps the views reachable during the GC.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static void LeakViews(int count)
        {
            for (var i = 0; i < count; i++)
                new RustView().Insert("x");
        }
    }
}
