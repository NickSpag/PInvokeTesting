using System;
using System.Buffers;

namespace PInvokeTesting
{
    /// <summary>
    /// Presents memory Rust owns as a <see cref="Memory{T}"/>, which is what a
    /// <see cref="ReadOnlySequence{T}"/> segment has to hold. It owns nothing:
    /// Dispose and Unpin are no-ops because Rust frees the bytes, and the GC
    /// never moves them, so there is nothing to pin.
    /// </summary>
    sealed unsafe class UnmanagedChunk : MemoryManager<byte>
    {
        readonly byte* _pointer;
        readonly int _length;

        public UnmanagedChunk(byte* pointer, int length)
        {
            _pointer = pointer;
            _length = length;
        }

        public override Span<byte> GetSpan() => new Span<byte>(_pointer, _length);

        public override MemoryHandle Pin(int elementIndex = 0) => new MemoryHandle(_pointer + elementIndex);

        public override void Unpin() { }

        protected override void Dispose(bool disposing) { }
    }

    /// <summary>One link in the chain; RunningIndex is where this chunk starts in the whole text.</summary>
    sealed class ChunkSegment : ReadOnlySequenceSegment<byte>
    {
        public ChunkSegment(ReadOnlyMemory<byte> memory) => Memory = memory;

        public ChunkSegment Append(ReadOnlyMemory<byte> memory)
        {
            var next = new ChunkSegment(memory) { RunningIndex = RunningIndex + Memory.Length };
            Next = next;
            return next;
        }
    }
}
