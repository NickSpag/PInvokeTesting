using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace PInvokeTesting
{
    /// <summary>One rope leaf as Rust lends it: matches rope::Chunk.</summary>
    [StructLayout(LayoutKind.Sequential)]
    struct NativeChunk
    {
        public byte* Pointer;
        public nuint Length;
    }

    /// <summary>
    /// Bindings for Lapce's rope (lapce-xi-rope). Same rules as RustBindings:
    /// every binding states whether calling it can break memory safety.
    /// </summary>
    public static partial class RopeBindings
    {
        private const string NativeLibrary = "ffi_test";

        public const int InvalidRange = 4;

        // safe: takes nothing, and hands back a pointer only inside a SafeHandle.
        [LibraryImport(NativeLibrary, EntryPoint = "rope_create")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static safe partial RopeHandle _rope_create();

        // unsafe: a raw pointer freed twice, or one Rust didn't hand out, corrupts the heap.
        [LibraryImport(NativeLibrary, EntryPoint = "rope_free")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static unsafe partial void _rope_free(IntPtr rope);

        // unsafe: Rust reads `len` bytes from `text` and can't check that they exist.
        // The range is checked on the Rust side.
        [LibraryImport(NativeLibrary, EntryPoint = "rope_edit")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static unsafe partial int _rope_edit(RopeHandle rope, nuint start, nuint end, byte* text, nuint len);

        // safe: no arguments that can lie; the SafeHandle keeps the rope alive.
        [LibraryImport(NativeLibrary, EntryPoint = "rope_delete_backward")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static safe partial int _rope_delete_backward(RopeHandle rope);

        // safe: reads a length. A few instructions: skip the GC transition.
        [SuppressGCTransition]
        [LibraryImport(NativeLibrary, EntryPoint = "rope_len")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static safe partial nuint _rope_len(RopeHandle rope);

        // unsafe: writes `capacity` chunks to `out`, and lends memory with a
        // lifetime ("until the next edit") the compiler can't see.
        [LibraryImport(NativeLibrary, EntryPoint = "rope_chunks")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static unsafe partial nuint _rope_chunks(RopeHandle rope, NativeChunk* output, nuint capacity);

        // safe: reads a counter.
        [SuppressGCTransition]
        [LibraryImport(NativeLibrary, EntryPoint = "live_ropes")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static safe partial nuint _live_ropes();


        public static RopeHandle Create() => _rope_create();

        /// <summary>Safe to call: the span carries its own length, and Rust checks the range.</summary>
        public static int Edit(RopeHandle rope, long start, long end, ReadOnlySpan<byte> utf8)
        {
            if (start < 0 || end < start)
                return InvalidRange;
            fixed (byte* text = utf8)
            {
                return unsafe(_rope_edit(rope, (nuint)start, (nuint)end, text, (nuint)utf8.Length));
            }
        }

        public static int DeleteBackward(RopeHandle rope) => _rope_delete_backward(rope);

        public static long Length(RopeHandle rope) => checked((long)_rope_len(rope));

        public static int LiveRopes => checked((int)_live_ropes());

        public static unsafe void Free(IntPtr rope)
        {
            unsafe { _rope_free(rope); }
        }

        /// <summary>
        /// Every leaf in one call, linked into a sequence without copying. Valid
        /// only until the next edit or until the rope is freed, which the caller
        /// must guarantee: TextBorrow does, by holding a reference on the handle.
        /// </summary>
        public static unsafe ReadOnlySequence<byte> BorrowText(RopeHandle rope)
        {
            // Leaves hold 511 to 1024 bytes, so this guess is almost always enough;
            // Rust reports the real count when it isn't, and the second call fits.
            var capacity = (int)Math.Min(int.MaxValue, Length(rope) / 511 + 2);
            while (true)
            {
                var buffer = ArrayPool<NativeChunk>.Shared.Rent(capacity);
                try
                {
                    int count;
                    fixed (NativeChunk* output = buffer)
                    {
                        count = checked((int)unsafe(_rope_chunks(rope, output, (nuint)buffer.Length)));
                    }
                    if (count > buffer.Length)
                    {
                        capacity = count;
                        continue;
                    }
                    return unsafe(Link(buffer.AsSpan(0, count)));
                }
                finally
                {
                    ArrayPool<NativeChunk>.Shared.Return(buffer);
                }
            }
        }

        static unsafe ReadOnlySequence<byte> Link(ReadOnlySpan<NativeChunk> chunks)
        {
            ChunkSegment first = null, last = null;
            foreach (var chunk in chunks)
            {
                // Sound for as long as BorrowText's caller keeps its promise.
                var memory = unsafe(new UnmanagedChunk(chunk.Pointer, checked((int)chunk.Length))).Memory;
                last = last == null ? (first = new ChunkSegment(memory)) : last.Append(memory);
            }
            return first == null
                ? ReadOnlySequence<byte>.Empty
                : new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
        }
    }

    public class RopeHandle : NativeTextHandle
    {
        internal override unsafe ReadOnlySequence<byte> ReadText() => unsafe(RopeBindings.BorrowText(this));

        protected override bool ReleaseHandle()
        {
            // Sound: the SafeHandle runs this exactly once, for a pointer Rust made.
            unsafe { RopeBindings.Free(handle); }
            return true;
        }
    }
}
