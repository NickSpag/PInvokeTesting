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

    public static unsafe partial class RustBindings
    {
        // Resolved next to the executable; the csproj copies the dylib there.
        private const string NativeLibrary = "ffi_test";

        public const int Ok = 0;
        public const int NullArgument = 1;
        public const int InvalidUtf8 = 2;
        public const int Panic = 3;

        [LibraryImport(NativeLibrary, EntryPoint = "create_view")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static partial RustViewHandle _create_view();

        [LibraryImport(NativeLibrary, EntryPoint = "free_view")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static partial void _free_view(IntPtr view);

        [LibraryImport(NativeLibrary, EntryPoint = "insert_text")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static partial int _insert_text(RustViewHandle view, byte* text, nuint len);

        // A few instructions that never block or call back: skip the GC transition.
        [SuppressGCTransition]
        [LibraryImport(NativeLibrary, EntryPoint = "view_chunk_count")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static partial nuint _view_chunk_count(RustViewHandle view);

        // A few instructions that never block or call back: skip the GC transition.
        [SuppressGCTransition]
        [LibraryImport(NativeLibrary, EntryPoint = "view_chunk")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static partial byte* _view_chunk(RustViewHandle view, nuint index, out nuint len);

        [LibraryImport(NativeLibrary, EntryPoint = "set_update_callback")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static partial int _set_update_callback(RustViewHandle view, delegate* unmanaged[Cdecl]<IntPtr, void> callback, IntPtr userData);

        // A few instructions that never block or call back: skip the GC transition.
        [SuppressGCTransition]
        [LibraryImport(NativeLibrary, EntryPoint = "live_views")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static partial nuint _live_views();


        public static int InsertUtf8(RustViewHandle view, ReadOnlySpan<byte> utf8)
        {
            fixed (byte* text = utf8)
            {
                return _insert_text(view, text, (nuint)utf8.Length);
            }
        }

        /// <summary>
        /// Links Rust's chunks into one sequence without copying a byte. Valid
        /// only until the next edit or until the view is freed; nothing enforces it.
        /// </summary>
        public static ReadOnlySequence<byte> BorrowText(RustViewHandle view)
        {
            var count = checked((int)_view_chunk_count(view));
            ChunkSegment first = null, last = null;
            for (var i = 0; i < count; i++)
            {
                var chunk = _view_chunk(view, (nuint)i, out var len);
                if (chunk == null)
                    break;
                var memory = new UnmanagedChunk(chunk, checked((int)len)).Memory;
                last = last == null ? (first = new ChunkSegment(memory)) : last.Append(memory);
            }
            return first == null
                ? ReadOnlySequence<byte>.Empty
                : new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
        }

        public static int SetUpdateCallback(RustViewHandle view, delegate* unmanaged[Cdecl]<IntPtr, void> callback, IntPtr userData)
            => _set_update_callback(view, callback, userData);

        public static void FreeView(IntPtr viewHandle)
        {
            _free_view(viewHandle);
        }

        public static RustViewHandle CreateView() => _create_view();

        public static int LiveViews => checked((int)_live_views());

    }
}
