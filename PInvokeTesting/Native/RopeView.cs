using System;
using System.Text;

namespace PInvokeTesting
{
    /// <summary>
    /// Text held in Lapce's rope, edited in place the way Lapce edits it: by
    /// replacing a byte range. Unlike RustView there's no update callback; Lapce's
    /// UI owns the rope and needs no one to tell it what it just did.
    /// </summary>
    public sealed class RopeView : IDisposable
    {
        readonly RopeHandle _rope;

        public RopeView()
        {
            _rope = RopeBindings.Create();
            if (_rope.IsInvalid)
                throw new InvalidOperationException("rope_create returned null");
        }

        /// <summary>The text's length in UTF-8 bytes.</summary>
        public long Length => RopeBindings.Length(_rope);

        /// <summary>Replaces the bytes start..end; both must fall on character boundaries.</summary>
        public void Edit(long start, long end, ReadOnlySpan<byte> utf8)
        {
            var status = RopeBindings.Edit(_rope, start, end, utf8);
            if (status == RopeBindings.InvalidRange)
                throw new ArgumentOutOfRangeException(nameof(start), $"{start}..{end} isn't a range of whole characters in the text");
            if (status == RustBindings.InvalidUtf8)
                throw new ArgumentException("text is not valid UTF-8", nameof(utf8));
            if (status != RustBindings.Ok)
                throw new InvalidOperationException($"rope_edit failed with status {status}");
        }

        public void Edit(long start, long end, string text) => Edit(start, end, Encoding.UTF8.GetBytes(text));

        /// <summary>Appends at the end of the text.</summary>
        public void Insert(ReadOnlySpan<byte> utf8)
        {
            var end = Length;
            Edit(end, end, utf8);
        }

        public void Insert(string text) => Insert(Encoding.UTF8.GetBytes(text));

        /// <summary>Removes the last character, like a backspace at the end of the text.</summary>
        public void DeleteBackward()
        {
            var status = RopeBindings.DeleteBackward(_rope);
            if (status != RustBindings.Ok)
                throw new InvalidOperationException($"rope_delete_backward failed with status {status}");
        }

        /// <summary>Reads the rope's leaves in place; dispose it before the next edit.</summary>
        public TextBorrow BorrowText() => new TextBorrow(_rope);

        /// <summary>Copies the text out, so nothing outlives Rust's buffers.</summary>
        public string Text
        {
            get
            {
                using (var text = BorrowText())
                    return text.ToString();
            }
        }

        public void Dispose() => _rope.Dispose();
    }
}
