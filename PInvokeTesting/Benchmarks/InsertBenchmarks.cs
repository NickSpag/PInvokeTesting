using BenchmarkDotNet.Attributes;

namespace PInvokeTesting.Benchmarks
{
    /// <summary>
    /// The question PInvokeTesting was written to answer: what does driving the
    /// Rust view in-process save over xi's usual separate process and JSON pipe?
    /// Both sides do the same work per operation: type a character and backspace
    /// it (so the text never grows), each edit answered with the new length.
    /// </summary>
    [MemoryDiagnoser]
    [InProcess] // the native library and binary sit next to this assembly
    public class InsertBenchmarks
    {
        RustView _view;
        RopeView _rope;
        RpcClient _rpc;

        [GlobalSetup]
        public void Setup()
        {
            _view = new RustView();
            _rope = new RopeView();
            _rpc = new RpcClient();
        }

        /// <summary>Two P/Invoke calls, each running the update callback into C#.</summary>
        [Benchmark(Baseline = true)]
        public long InProcess()
        {
            _view.Insert("x"u8);
            _view.DeleteBackward();
            return _view.Length;
        }

        /// <summary>
        /// The same two edits on Lapce's rope. No update callback: Lapce's UI owns
        /// the rope, so nothing needs telling.
        /// </summary>
        [Benchmark]
        public long RopeInProcess()
        {
            _rope.Insert("x"u8);
            _rope.DeleteBackward();
            return _rope.Length;
        }

        /// <summary>Two JSON round trips through the Rust process's stdin and stdout.</summary>
        [Benchmark]
        public long OverPipe()
        {
            _rpc.Insert();
            return _rpc.DeleteBackward();
        }

        [GlobalCleanup]
        public void Cleanup()
        {
            _rpc?.Dispose();
            _rope?.Dispose();
            _view?.Dispose();
        }
    }
}
