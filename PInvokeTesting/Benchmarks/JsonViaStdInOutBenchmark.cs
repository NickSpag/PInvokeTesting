using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using BenchmarkDotNet.Attributes;

namespace PInvokeTesting.Benchmarks
{
    public class TestMessage
    {
        public string Id { get; set; } = "view-id-1";
    }

    public class JsonViaStdInOutBenchmark
    {
        const string rustLoopExecutable = "/Users/nickspagnola/Projects/PInvokeTesting/rust/target/release/ffi_test_bin";

        readonly Process _process;
        readonly Stream _input;

        ManualResetEvent stdout_completed = new ManualResetEvent(false);
        ManualResetEvent stderr_completed = new ManualResetEvent(false);

        #region Construction and Configuration
        public JsonViaStdInOutBenchmark()
        {
            var startInfo = ConfigureStartInfo();
            _process = Process.Start(startInfo);
            //_process.BeginErrorReadLine();
            //_process.BeginOutputReadLine();

            _input = _process.StandardInput.BaseStream;

            ConfigureProcess(_process);
        }

        private ProcessStartInfo ConfigureStartInfo()
        {
            var startInfo = new ProcessStartInfo();

            startInfo.FileName = rustLoopExecutable;

            startInfo.CreateNoWindow = true;
            startInfo.UseShellExecute = false;

            //startInfo.StandardOutputEncoding = Encoding.UTF8;
            //startInfo.StandardErrorEncoding = Encoding.UTF8;

            //startInfo.RedirectStandardError = true;
            startInfo.RedirectStandardInput = true;
            //startInfo.RedirectStandardOutput = true;

            return startInfo;
        }

        private void ConfigureProcess(Process process)
        {
            process.EnableRaisingEvents = true;

            process.ErrorDataReceived += Process_ErrorDataReceived;
            process.OutputDataReceived += Process_OutputDataReceived;
            process.Exited += Process_Exited;
        }
        #endregion

        [Benchmark]
        public void SendSerializedObject()
        {
            var testMessage = JsonSerializer.Serialize(new TestMessage()) + '\n';
            var bytes = Encoding.UTF8.GetBytes(testMessage);

            _input.Write(bytes, 0, bytes.Length);
            _input.Flush();
        }

        [GlobalCleanup]
        public void Cleanup()
        {
            _process?.Close();
            _input?.Dispose();
        }

        private void HandleRawDataReceived(string data)
        {
            System.Console.WriteLine($"RECEIVED: {data}");
        }

        #region Event Handlers
        private void Process_OutputDataReceived(object sender, DataReceivedEventArgs e)
        {
            if (e.Data is null)
            {
                System.Console.WriteLine("OutputStream: null received, resetting");
                stdout_completed.Set();
            }
            else
            {
                HandleRawDataReceived(e.Data);
            }
        }

        private void Process_Exited(object sender, EventArgs e)
        {
            System.Console.WriteLine("Exited");
        }

        private void Process_ErrorDataReceived(object sender, DataReceivedEventArgs e)
        {
            if (e.Data is null)
            {
                System.Console.WriteLine("ErrorStream: null received, resetting");
                stderr_completed.Set();
            }
            else
            {
                System.Console.WriteLine("ErrorStream: " + e.Data);
            }
        }
        #endregion

    }
}
