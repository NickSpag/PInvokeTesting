using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PInvokeTesting.Benchmarks
{
    /// <summary>
    /// The out-of-process way to drive the same RustView: start ffi_test_bin, send
    /// one JSON request per line, wait for its JSON reply. This is how xi-core
    /// talks to its frontends.
    /// </summary>
    public sealed class RpcClient : IDisposable
    {
        static readonly Request InsertX = new Request { Method = "insert", Params = new InsertParams { Chars = "x" } };
        static readonly Request Backspace = new Request { Method = "delete_backward" };

        readonly Process _process;
        readonly Stream _stdin;
        readonly StreamReader _stdout;

        public RpcClient()
        {
            var startInfo = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "ffi_test_bin"))
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
            };
            _process = Process.Start(startInfo) ?? throw new InvalidOperationException("ffi_test_bin didn't start");
            _stdin = _process.StandardInput.BaseStream;
            _stdout = _process.StandardOutput;
        }

        /// <summary>Inserts "x" and returns the new length, in UTF-8 bytes.</summary>
        public long Insert() => Send(InsertX);

        /// <summary>Removes the last character and returns the new length.</summary>
        public long DeleteBackward() => Send(Backspace);

        long Send(Request request)
        {
            JsonSerializer.Serialize(_stdin, request, RpcJson.Default.Request);
            _stdin.WriteByte((byte)'\n');
            _stdin.Flush();

            var line = _stdout.ReadLine() ?? throw new EndOfStreamException("ffi_test_bin closed its output");
            var reply = JsonSerializer.Deserialize(line, RpcJson.Default.Reply);
            if (reply?.Method != "update")
                throw new InvalidOperationException($"ffi_test_bin replied: {line}");
            return reply.Params.Len;
        }

        public void Dispose()
        {
            // Closing stdin ends the Rust loop.
            _stdin.Dispose();
            if (!_process.WaitForExit(TimeSpan.FromSeconds(5)))
                _process.Kill();
            _process.Dispose();
        }
    }

    public sealed class Request
    {
        public string Method { get; init; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public InsertParams Params { get; init; }
    }

    public sealed class InsertParams
    {
        public string Chars { get; init; }
    }

    public sealed class Reply
    {
        public string Method { get; init; }
        public ReplyParams Params { get; init; }
    }

    public sealed class ReplyParams
    {
        public long Len { get; init; }
        public string Message { get; init; }
    }

    // Source-generated, so serialization needs no reflection (and works under NativeAOT).
    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
    [JsonSerializable(typeof(Request))]
    [JsonSerializable(typeof(Reply))]
    partial class RpcJson : JsonSerializerContext
    {
    }
}
