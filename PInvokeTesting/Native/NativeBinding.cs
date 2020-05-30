using System;
using System.Runtime.InteropServices;

namespace PInvokeTesting
{
    /// <summary>
    /// I want direct access to the rust View. Giving it a callback, so that it can directly send notifications like Update/ScrollTo/etc
    /// </summary>

    public static class RustBindings
    {
        private const string NativeLibrary = "/Users/nickspagnola/Projects/PInvokeTesting/rust/target/release/lib_ffi_test.dylib";

        [DllImport(NativeLibrary, EntryPoint = "create_view", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
        private static extern RustViewHandle _create_view();

        [DllImport(NativeLibrary, EntryPoint = "free_view", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
        private static extern void _free_view(IntPtr view);

        [DllImport(NativeLibrary, EntryPoint = "insert", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
        private static extern void _insert(RustViewHandle view, string text);


        public static void Insert(RustViewHandle view, string text)
        {
            _insert(view, text);
        }

        public static void FreeView(IntPtr viewHandle)
        {
            _free_view(viewHandle);
        }

        public static RustViewHandle CreateView() => _create_view();

    }
}
