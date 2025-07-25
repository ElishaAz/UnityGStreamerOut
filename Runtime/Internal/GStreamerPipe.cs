using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Unity.Collections;

namespace GStreamerOut
{
    public sealed class GStreamerPipe : System.IDisposable
    {
        #region Public methods

        public static bool IsAvailable(string executable)
        {
            return ExecutablePath(executable) != null;
        }

        public GStreamerPipe(string executable, string arguments)
        {
            // Start GStreamer subprocess.
            _subprocess = Process.Start(new ProcessStartInfo
            {
                FileName = ExecutablePath(executable),
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });

            // Start copy/pipe subthreads.
            _copyThread = new Thread(CopyThread);
            _pipeThread = new Thread(PipeThread);
            _copyThread.Start();
            _pipeThread.Start();
        }

        public void PushFrameData(NativeArray<byte> data)
        {
            // Update the copy queue and notify the copy thread with a ping.
            lock (_copyQueue) _copyQueue.Enqueue(data);
            _copyPing.Set();
        }

        public void SyncFrameData()
        {
            // Wait for the copy queue to get emptied with using pong
            // notification signals sent from the copy thread.
            while (_copyQueue.Count > 0) _copyPong.WaitOne();

            // When using a slower codec (e.g. HEVC, ProRes), frames may be
            // queued too much, and it may end up with an out-of-memory error.
            // To avoid this problem, we wait for pipe queue entries to be
            // comsumed by the pipe thread.
            while (_pipeQueue.Count > 4) _pipePong.WaitOne();
        }

        public string CloseAndGetOutput()
        {
            // Terminate the subthreads.
            _terminate = true;

            _copyPing.Set();
            _pipePing.Set();

            _copyThread.Join();
            _pipeThread.Join();

            // Close GStreamer subprocess.
            _subprocess.StandardInput.Close();
            _subprocess.WaitForExit();

            var outputReader = _subprocess.StandardError;
            var error = outputReader.ReadToEnd();

            _subprocess.Close();
            _subprocess.Dispose();

            outputReader.Close();
            outputReader.Dispose();

            // Nullify members (just for ease of debugging).
            _subprocess = null;
            _copyThread = null;
            _pipeThread = null;
            _copyQueue = null;
            _pipeQueue = _freeBuffer = null;

            return error;
        }

        #endregion

        #region IDisposable implementation

        public void Dispose()
        {
            if (!_terminate) CloseAndGetOutput();
        }

        ~GStreamerPipe()
        {
            if (!_terminate)
                UnityEngine.Debug.LogError(
                    "An unfinalized GStreamerPipe object was detected. " +
                    "It should be explicitly closed or disposed " +
                    "before being garbage-collected."
                );
        }

        #endregion

        #region Private members

        private Process _subprocess;
        private Thread _copyThread;
        private Thread _pipeThread;

        private AutoResetEvent _copyPing = new AutoResetEvent(false);
        private AutoResetEvent _copyPong = new AutoResetEvent(false);
        private AutoResetEvent _pipePing = new AutoResetEvent(false);
        private AutoResetEvent _pipePong = new AutoResetEvent(false);
        private bool _terminate;

        private Queue<NativeArray<byte>> _copyQueue = new Queue<NativeArray<byte>>();
        private Queue<byte[]> _pipeQueue = new Queue<byte[]>();
        private Queue<byte[]> _freeBuffer = new Queue<byte[]>();

        private static string ExecutablePath(string executable = "gst-launch-1.0")
        {
            if (File.Exists(executable))
                return Path.GetFullPath(executable);

            var values = Environment.GetEnvironmentVariable("PATH");
            foreach (var path in values.Split(Path.PathSeparator))
            {
                var fullPath = Path.Combine(path, executable);
                if (File.Exists(fullPath))
                    return fullPath;
            }

            return null;
        }

        #endregion

        #region Subthread entry points

        // CopyThread - Copies frames given from the readback queue to the pipe
        // queue. This is required because readback buffers are not under our
        // control -- they'll be disposed before being processed by us. They
        // have to be buffered by end-of-frame.
        void CopyThread()
        {
            while (!_terminate)
            {
                // Wait for ping from the main thread.
                _copyPing.WaitOne();

                // Process all entries in the copy queue.
                while (_copyQueue.Count > 0)
                {
                    // Retrieve an copy queue entry without dequeuing it.
                    // (We don't want to notify the main thread at this point.)
                    NativeArray<byte> source;
                    lock (_copyQueue) source = _copyQueue.Peek();

                    // Try allocating a buffer from the free buffer list.
                    byte[] buffer = null;
                    if (_freeBuffer.Count > 0)
                        lock (_freeBuffer)
                            buffer = _freeBuffer.Dequeue();

                    // Copy the contents of the copy queue entry.
                    if (buffer == null || buffer.Length != source.Length)
                        buffer = source.ToArray();
                    else
                        source.CopyTo(buffer);

                    // Push the buffer entry to the pipe queue.
                    lock (_pipeQueue) _pipeQueue.Enqueue(buffer);
                    _pipePing.Set(); // Ping the pipe thread.

                    // Dequeue the copy buffer entry and ping the main thread.
                    lock (_copyQueue) _copyQueue.Dequeue();
                    _copyPong.Set();
                }
            }
        }

        // PipeThread - Receives frame entries from the copy thread and push
        // them into the GStreamer pipe.
        void PipeThread()
        {
            var pipe = _subprocess.StandardInput.BaseStream;

            while (!_terminate)
            {
                // Wait for the ping from the copy thread.
                _pipePing.WaitOne();

                // Process all entries in the pipe queue.
                while (_pipeQueue.Count > 0)
                {
                    // Retrieve a frame entry.
                    byte[] buffer;
                    lock (_pipeQueue) buffer = _pipeQueue.Dequeue();

                    // Write it into the GStreamer pipe.
                    try
                    {
                        pipe.Write(buffer, 0, buffer.Length);
                        pipe.Flush();
                    }
                    catch
                    {
                        // Pipe.Write could raise an IO exception when GStreamer
                        // is terminated for some reason. We just ignore this
                        // situation and assume that it will be resolved in the
                        // main thread. #badcode
                    }

                    // Add the buffer to the free buffer list to reuse later.
                    lock (_freeBuffer) _freeBuffer.Enqueue(buffer);
                    _pipePong.Set();
                }
            }
        }

        #endregion
    }
}