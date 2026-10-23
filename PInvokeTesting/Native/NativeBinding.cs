using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

// Nothing in this assembly may rely on the runtime's built-in marshalling: every
// signature must be blittable, or be handled by the LibraryImport generator.
[assembly: DisableRuntimeMarshalling]

namespace PInvokeTesting
{
    /// <summary>
    /// I want direct access to the rust View. Giving it a callback, so that it can directly send notifications like Update/ScrollTo/etc
    /// </summary>
    /// <remarks>
    /// Under the C# 15 memory safety rules every binding states whether calling
    /// it can break memory safety: `unsafe` puts the audit on the caller, `safe`
    /// is a promise that no arguments can. The compiler only insists on it for a
    /// binding the generator emits as a plain extern; it's written on all of
    /// them so the audit boundary is visible in one place.
    /// </remarks>
    public static partial class RustBindings
    {
        // Resolved next to the executable; the csproj copies the dylib there.
        private const string NativeLibrary = "ffi_test";

        public const int Ok = 0;
        public const int NullArgument = 1;
        public const int InvalidUtf8 = 2;
        public const int Panic = 3;

        // safe: takes nothing, and hands back a pointer only inside a SafeHandle.
        [LibraryImport(NativeLibrary, EntryPoint = "create_view")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static safe partial RustViewHandle _create_view();

        // unsafe: a raw pointer freed twice, or one Rust didn't hand out, corrupts the heap.
        [LibraryImport(NativeLibrary, EntryPoint = "free_view")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static unsafe partial void _free_view(IntPtr view);

        // unsafe: Rust reads `len` bytes from `text` and can't check that they exist.
        [LibraryImport(NativeLibrary, EntryPoint = "insert_text")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static unsafe partial int _insert_text(RustViewHandle view, byte* text, nuint len);

        // safe: reads a length; the SafeHandle keeps the view alive for the call.
        // A few instructions that never block or call back: skip the GC transition.
        [SuppressGCTransition]
        [LibraryImport(NativeLibrary, EntryPoint = "view_chunk_count")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static safe partial nuint _view_chunk_count(RustViewHandle view);

        // unsafe: lends Rust's memory with a lifetime ("until the next edit") the
        // compiler can't see. Reading it is unsafe anyway; this marks who must
        // keep the promise.
        // A few instructions that never block or call back: skip the GC transition.
        [SuppressGCTransition]
        [LibraryImport(NativeLibrary, EntryPoint = "view_chunk")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static unsafe partial byte* _view_chunk(RustViewHandle view, nuint index, out nuint len);

        // unsafe: Rust will later call `callback` with `userData`; a wrong one is a jump into nowhere.
        [LibraryImport(NativeLibrary, EntryPoint = "set_update_callback")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static unsafe partial int _set_update_callback(RustViewHandle view, delegate* unmanaged[Cdecl]<IntPtr, void> callback, IntPtr userData);

        // safe: reads a counter.
        // A few instructions that never block or call back: skip the GC transition.
        [SuppressGCTransition]
        [LibraryImport(NativeLibrary, EntryPoint = "live_views")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static safe partial nuint _live_views();


        /// <summary>Safe to call: the span carries its own length, so the audit is done here.</summary>
        public static int InsertUtf8(RustViewHandle view, ReadOnlySpan<byte> utf8)
        {
            // `fixed` no longer needs an unsafe context; passing the pointer on does.
            fixed (byte* text = utf8)
            {
                return unsafe(_insert_text(view, text, (nuint)utf8.Length));
            }
        }

        /// <summary>
        /// Links Rust's chunks into one sequence without copying a byte. Valid
        /// only until the next edit or until the view is freed, which the caller
        /// must guarantee: TextBorrow does, by holding a reference on the handle.
        /// </summary>
        public static unsafe ReadOnlySequence<byte> BorrowText(RustViewHandle view)
        {
            var count = checked((int)_view_chunk_count(view));
            ChunkSegment first = null, last = null;
            for (var i = 0; i < count; i++)
            {
                // The caller guarantees the view outlives the sequence and isn't
                // edited meanwhile, which is the contract _view_chunk lends under.
                var chunk = unsafe(_view_chunk(view, (nuint)i, out var len));
                if (chunk == null)
                    break;
                var memory = unsafe(new UnmanagedChunk(chunk, checked((int)len))).Memory;
                last = last == null ? (first = new ChunkSegment(memory)) : last.Append(memory);
            }
            return first == null
                ? ReadOnlySequence<byte>.Empty
                : new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
        }

        public static unsafe int SetUpdateCallback(RustViewHandle view, delegate* unmanaged[Cdecl]<IntPtr, void> callback, IntPtr userData)
            => unsafe(_set_update_callback(view, callback, userData));

        public static unsafe void FreeView(IntPtr viewHandle)
        {
            unsafe { _free_view(viewHandle); }
        }

        public static RustViewHandle CreateView() => _create_view();

        public static int LiveViews => checked((int)_live_views());

    }
}
