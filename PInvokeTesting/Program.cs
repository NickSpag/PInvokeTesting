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

                // 28 bytes so far; "x" leaves 3 bytes of room in the 16-byte chunk, and
                // the second "é" lands with one byte to spare, where it can't fit.
                view.Insert("xéé");
                using (var text = view.BorrowText())
                {
                    var chunks = 0;
                    var everyChunkValid = true;
                    var strict = new UTF8Encoding(false, throwOnInvalidBytes: true);
                    foreach (var chunk in text)
                    {
                        chunks++;
                        try { strict.GetCharCount(chunk); }
                        catch (DecoderFallbackException) { everyChunkValid = false; }
                    }
                    Check(chunks > 1, $"text is read in place from {chunks} Rust chunks, not one buffer");
                    Check(text.Length == Encoding.UTF8.GetByteCount(text.ToString()), "borrow length matches the UTF-8 byte count");
                    Check(everyChunkValid, "no character is split across two chunks");
                }

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

            // A borrow outlives the view's Dispose: the free waits for it.
            var borrowed = new RustView();
            var borrow = borrowed.BorrowText();
            var copy = borrow;
            borrowed.Dispose();
            Check(RustBindings.LiveViews == 1, "an open borrow keeps a disposed view alive");
            Check(borrow.ToString() == "hey dotnet!", "and its text is still readable");
            borrow.Dispose();
            copy.Dispose();
            Check(RustBindings.LiveViews == 0, "closing the borrow frees it, once, even when the borrow was copied");

            try
            {
                _ = borrowed.Text;
                Check(false, "using a disposed view throws");
            }
            catch (ObjectDisposedException)
            {
                Check(true, "using a disposed view throws ObjectDisposedException rather than reading freed memory");
            }

#if DEMO_ESCAPE
            // Each line is a compile error: a borrow can't leave this method.
            object boxed = borrowed.BorrowText();
            Func<string> later = () => borrow.ToString();
            System.Threading.Tasks.Task<string> async = ReadLaterAsync(borrowed);
#endif

            // Whatever Rust holds to reach C# must survive a collection.
            using (var view = new RustView())
            {
                string afterGc = null;
                view.Updated += text => afterGc = text;
                GC.Collect();
                GC.WaitForPendingFinalizers();
                view.Insert("!");
                Check(afterGc == "hey dotnet!!", "callback still fires after a full GC");
            }

            return failures == 0 ? 0 : 1;
        }

#if DEMO_ESCAPE
        static async System.Threading.Tasks.Task<string> ReadLaterAsync(RustView view)
        {
            var text = view.BorrowText();
            await System.Threading.Tasks.Task.Yield();
            return text.ToString();
        }
#endif

        // Separate method so no local keeps the views reachable during the GC.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static void LeakViews(int count)
        {
            for (var i = 0; i < count; i++)
                new RustView().Insert("x");
        }
    }
}
