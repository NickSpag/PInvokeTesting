using System;
using System.Buffers;
using System.Text;
using System.Threading;

namespace PInvokeTesting
{
    /// <summary>
    /// Read access to a view's text, in place. While a borrow is open the view
    /// can't be freed: it holds a reference on the SafeHandle, so a Dispose of
    /// the view (or its finalizer) only frees it once the borrow ends.
    ///
    /// Being a ref struct, it can't be stored in a field, boxed, captured by a
    /// lambda or kept across an await, so it can't outlive the method that
    /// opened it. Within that method, reading after Dispose is still on you.
    /// Don't edit the view while a borrow is open: an edit invalidates it.
    /// </summary>
    public ref struct TextBorrow
    {
        readonly Lease _lease;
        readonly ReadOnlySequence<byte> _text;

        internal TextBorrow(RustViewHandle handle)
        {
            _lease = new Lease(handle);
            // Sound: the lease keeps the view from being freed until Dispose. That
            // an edit also invalidates the text is documented, not enforced.
            _text = unsafe(RustBindings.BorrowText(handle));
        }

        public long Length => _text.Length;

        public void CopyTo(Span<byte> destination) => _text.CopyTo(destination);

        // .NET 5 decodes across the segments directly, with no intermediate array.
        public override string ToString() => Encoding.UTF8.GetString(_text);

        /// <summary>Each Rust chunk as a span, which can't escape either.</summary>
        public ChunkEnumerator GetEnumerator() => new ChunkEnumerator(_text);

        public void Dispose() => _lease?.Release();

        public ref struct ChunkEnumerator
        {
            ReadOnlySequence<byte>.Enumerator _inner;

            internal ChunkEnumerator(ReadOnlySequence<byte> text) => _inner = text.GetEnumerator();

            public ReadOnlySpan<byte> Current => _inner.Current.Span;

            public bool MoveNext() => _inner.MoveNext();
        }

        /// <summary>
        /// A ref struct can still be copied, and every copy would call Dispose.
        /// The reference lives on the heap so it's released exactly once.
        /// </summary>
        sealed class Lease
        {
            readonly RustViewHandle _handle;
            int _released;

            public Lease(RustViewHandle handle)
            {
                var added = false;
                handle.DangerousAddRef(ref added);
                _handle = handle;
            }

            public void Release()
            {
                if (Interlocked.Exchange(ref _released, 1) == 0)
                    _handle.DangerousRelease();
            }
        }
    }
}
