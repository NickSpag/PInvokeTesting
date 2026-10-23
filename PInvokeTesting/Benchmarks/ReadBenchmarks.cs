using System;
using BenchmarkDotNet.Attributes;

namespace PInvokeTesting.Benchmarks
{
    /// <summary>
    /// Reading the whole text back from Rust: walking its chunks in place, and
    /// copying it into a string. The 16-byte toy chunks against Lapce's rope,
    /// whose leaves hold 511 to 1024 bytes.
    /// </summary>
    [MemoryDiagnoser]
    [InProcess]
    public class ReadBenchmarks
    {
        public enum Store { Chunks16, LapceRope }

        RustView _view;
        RopeView _rope;

        [Params(Store.Chunks16, Store.LapceRope)]
        public Store Text;

        [Params(1_024, 65_536, 1_048_576)]
        public int Bytes;

        [GlobalSetup]
        public void Setup()
        {
            var block = new byte[1024];
            Array.Fill(block, (byte)'a');
            if (Text == Store.Chunks16)
            {
                _view = new RustView();
                for (var length = 11; length < Bytes; length += block.Length)
                    _view.Insert(block);
            }
            else
            {
                _rope = new RopeView();
                for (var length = 11; length < Bytes; length += block.Length)
                    _rope.Insert(block);
            }
        }

        TextBorrow Borrow() => Text == Store.Chunks16 ? _view.BorrowText() : _rope.BorrowText();

        /// <summary>Visits every byte in place, through a borrow.</summary>
        [Benchmark]
        public long WalkChunks()
        {
            long total = 0;
            using var text = Borrow();
            foreach (var chunk in text)
                total += chunk.Length;
            return total;
        }

        /// <summary>Copies the whole text into a .NET string.</summary>
        [Benchmark]
        public int ToStringCopy() => (Text == Store.Chunks16 ? _view.Text : _rope.Text).Length;

        [GlobalCleanup]
        public void Cleanup()
        {
            _view?.Dispose();
            _rope?.Dispose();
        }
    }
}
