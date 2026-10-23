using System;
using System.Runtime.InteropServices;

namespace PInvokeTesting
{
    /// <summary>
    /// I want direct access to the rust View. Giving it a callback, so that it can directly send notifications like Update/ScrollTo/etc
    /// </summary>

    public static unsafe class RustBindings
    {
        // Resolved next to the executable; the csproj copies the dylib there.
        private const string NativeLibrary = "ffi_test";

        public const int Ok = 0;
        public const int NullArgument = 1;
        public const int InvalidUtf8 = 2;
        public const int Panic = 3;

        /// <summary>
        /// Rust calls this after every edit. In C# 8 the only way to hand managed
        /// code to native code is a delegate: the marshaller makes a native thunk
        /// for it, which stays valid only while the delegate object is alive.
        /// </summary>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void UpdateCallback(IntPtr userData, byte* text, UIntPtr len);

        [DllImport(NativeLibrary, EntryPoint = "create_view", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
        private static extern RustViewHandle _create_view();

        [DllImport(NativeLibrary, EntryPoint = "free_view", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
        private static extern void _free_view(IntPtr view);

        [DllImport(NativeLibrary, EntryPoint = "insert_text", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
        private static extern int _insert_text(RustViewHandle view, byte* text, UIntPtr len);

        [DllImport(NativeLibrary, EntryPoint = "view_text", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
        private static extern byte* _view_text(RustViewHandle view, out UIntPtr len);

        [DllImport(NativeLibrary, EntryPoint = "set_update_callback", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
        private static extern int _set_update_callback(RustViewHandle view, UpdateCallback callback, IntPtr userData);

        [DllImport(NativeLibrary, EntryPoint = "live_views", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
        private static extern UIntPtr _live_views();


        public static int InsertUtf8(RustViewHandle view, ReadOnlySpan<byte> utf8)
        {
            fixed (byte* text = utf8)
            {
                return _insert_text(view, text, (UIntPtr)utf8.Length);
            }
        }

        public static ReadOnlySpan<byte> BorrowText(RustViewHandle view)
        {
            var text = _view_text(view, out var len);
            return text == null ? default : new ReadOnlySpan<byte>(text, checked((int)len));
        }

        public static int SetUpdateCallback(RustViewHandle view, UpdateCallback callback, IntPtr userData)
            => _set_update_callback(view, callback, userData);

        public static void FreeView(IntPtr viewHandle)
        {
            _free_view(viewHandle);
        }

        public static RustViewHandle CreateView() => _create_view();

        public static int LiveViews => checked((int)_live_views());

    }
}
