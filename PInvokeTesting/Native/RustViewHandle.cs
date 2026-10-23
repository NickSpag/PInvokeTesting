using System;
using System.Runtime.InteropServices;
using System.Text;

namespace PInvokeTesting
{
    public unsafe class RustView : IDisposable
    {
        readonly RustViewHandle _rustView;

        /// <summary>Raised by Rust after every edit, on the thread that made it.</summary>
        public event Action<string> Updated;

        public RustView()
        {
            _rustView = RustBindings.CreateView();
            if (_rustView.IsInvalid)
                throw new InvalidOperationException("create_view returned null");

            // Rust holds this as an opaque pointer and hands it back to OnUpdate.
            // Weak, so the registration alone doesn't keep the view alive.
            // The SafeHandle owns it, so it's freed with the view even when nobody disposes.
            _rustView.Owner = GCHandle.Alloc(this, GCHandleType.Weak);
            RustBindings.SetUpdateCallback(_rustView, s_onUpdate, GCHandle.ToIntPtr(_rustView.Owner));
        }

        public void Insert(string text) => Insert(Encoding.UTF8.GetBytes(text));

        public void Insert(ReadOnlySpan<byte> utf8)
        {
            var status = RustBindings.InsertUtf8(_rustView, utf8);
            if (status == RustBindings.InvalidUtf8)
                throw new ArgumentException("text is not valid UTF-8", nameof(utf8));
            if (status != RustBindings.Ok)
                throw new InvalidOperationException($"insert_text failed with status {status}");
        }

        /// <summary>Reads Rust's chunks in place; dispose it before the next edit.</summary>
        public TextBorrow BorrowText() => new TextBorrow(_rustView);

        /// <summary>Copies the text out, so nothing outlives Rust's buffer.</summary>
        public string Text
        {
            get
            {
                using (var text = BorrowText())
                    return text.ToString();
            }
        }

        // Rust keeps the native thunk for as long as any view lives, so the
        // delegate behind it must never be collected: one static instance,
        // shared by every view. A delegate created per call and dropped is the
        // classic crash here, because nothing on the managed side references it.
        static readonly RustBindings.UpdateCallback s_onUpdate = OnUpdate;

        static void OnUpdate(IntPtr userData)
        {
            // An exception may not cross back into Rust either.
            try
            {
                if (GCHandle.FromIntPtr(userData).Target is RustView view)
                    view.Updated?.Invoke(view.Text);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Update handler threw: {ex}");
            }
        }

        public void Dispose()
        {
            _rustView.Dispose();
        }

    }

    public class RustViewHandle : SafeHandle
    {
        public RustViewHandle() : base(IntPtr.Zero, true)
        { }

        /// <summary>The managed RustView that Rust passes back to the update callback.</summary>
        internal GCHandle Owner;

        public override bool IsInvalid => handle == IntPtr.Zero;

        protected override bool ReleaseHandle()
        {
            // Free the view first: after this Rust can no longer call back with Owner.
            RustBindings.FreeView(handle);
            if (Owner.IsAllocated)
                Owner.Free();
            return true;
        }
    }
}
