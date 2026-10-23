using System;
using System.Text;
#if BENCHMARKS
using BenchmarkDotNet.Running;
#endif
using PInvokeTesting.Benchmarks;

namespace PInvokeTesting
{
    class Program
    {
        static int Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "bench")
            {
#if BENCHMARKS
                BenchmarkRunner.Run<InsertBenchmarks>();
                BenchmarkRunner.Run<ReadBenchmarks>();
                return 0;
#else
                Console.Error.WriteLine("Benchmarks need the JIT: run `dotnet run -c Release -- bench` instead.");
                return 2;
#endif
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
                view.Insert("xéé"u8);
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

                view.Insert("a\0b"u8);
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

            using (var view = new RustView())
            {
                view.Insert("é"u8);
                view.DeleteBackward();
                Check(view.Text == "hey dotnet!" && view.Length == 11, "backspace removes a whole two-byte character");
            }

            // Lapce's rope, through the same handle and borrow design.
            using (var rope = new RopeView())
            {
                Check(rope.Text == "hey dotnet!", "rope: initial text reads back");

                rope.Edit(4, 10, "lapce");
                Check(rope.Text == "hey lapce!", $"rope: replacing a range in the middle: \"{rope.Text}\"");

                rope.Insert(" — 🦀");
                rope.DeleteBackward();
                Check(rope.Text == "hey lapce! — " && rope.Length == 15, "rope: backspace removes a whole four-byte character");

                try
                {
                    rope.Edit(12, 13, "x"); // byte 12 is inside "—"
                    Check(false, "rope: an edit splitting a character is refused");
                }
                catch (ArgumentOutOfRangeException)
                {
                    Check(rope.Text == "hey lapce! — ", "rope: an edit splitting a character is refused, not applied");
                }

                // Grow it past a few leaves, mixing in multi-byte characters.
                var line = Encoding.UTF8.GetBytes("héllo, rope — 🦀 line of text\n");
                for (var i = 0; i < 200; i++)
                    rope.Insert(line);

                using (var text = rope.BorrowText())
                {
                    int leaves = 0, largest = 0;
                    var everyLeafValid = true;
                    var strict = new UTF8Encoding(false, throwOnInvalidBytes: true);
                    foreach (var leaf in text)
                    {
                        leaves++;
                        largest = Math.Max(largest, leaf.Length);
                        try { strict.GetCharCount(leaf); }
                        catch (DecoderFallbackException) { everyLeafValid = false; }
                    }
                    Check(leaves > 1 && largest <= 1024, $"rope: {text.Length} bytes read in place from {leaves} leaves of at most {largest} bytes");
                    Check(everyLeafValid, "rope: no character is split across two leaves");
                    Check(text.Length == rope.Length, "rope: the leaves add up to its length");
                }

                var ropeBorrow = rope.BorrowText();
                rope.Dispose();
                Check(RopeBindings.LiveRopes == 1, "rope: an open borrow keeps a disposed rope alive");
                ropeBorrow.Dispose();
            }
            Check(RopeBindings.LiveRopes == 0, "rope: closing the borrow frees it");

            LeakRopes(1000);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            Check(RopeBindings.LiveRopes == 0, $"rope: finalizer freed every undisposed rope (live: {RopeBindings.LiveRopes})");

            // The same edits over the pipe, as the benchmark makes them.
            using (var rpc = new PInvokeTesting.Benchmarks.RpcClient())
            {
                var afterInsert = rpc.Insert();
                var afterBackspace = rpc.DeleteBackward();
                Check(afterInsert == 12 && afterBackspace == 11, $"JSON round trips through ffi_test_bin (lengths {afterInsert}, {afterBackspace})");
            }

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

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static void LeakRopes(int count)
        {
            for (var i = 0; i < count; i++)
                new RopeView().Insert("x");
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
