using UnityEngine;
using UnityEngine.Rendering;
using System.Collections.Generic;

namespace GStreamerOut
{
    public sealed class GStreamerSession : System.IDisposable
    {
        #region Factory methods

        public static GStreamerSession Create(string executable, int width, int height, float frameRate,
            string pipeline)
        {
            return new GStreamerSession(executable,
                $"fdsrc fd=0 do-timestamp=true is-live=true ! rawvideoparse format=rgba width={width} height={height} framerate={frameRate} ! {pipeline}");
        }

        #endregion

        #region Public properties and members

        public void PushFrame(Texture source)
        {
            if (_pipe != null)
            {
                ProcessQueue();
                if (source != null) QueueFrame(source);
            }
        }

        public void CompletePushFrames()
        {
            _pipe?.SyncFrameData();
        }

        public void Close()
        {
            if (_pipe != null)
            {
                var error = _pipe.CloseAndGetOutput();

                if (!string.IsNullOrEmpty(error))
                    Debug.LogWarning(
                        "GStreamer returned with warning/error messages. " +
                        "See the following lines for details:\n" + error
                    );

                _pipe.Dispose();
                _pipe = null;
            }

            if (_blitMaterial != null)
            {
                UnityEngine.Object.Destroy(_blitMaterial);
                _blitMaterial = null;
            }
        }

        public void Dispose()
        {
            Close();
        }

        #endregion

        #region Private objects and constructor/destructor

        private GStreamerPipe _pipe;
        private Material _blitMaterial;

        private GStreamerSession(string executable, string arguments)
        {
            if (!GStreamerPipe.IsAvailable(executable))
                Debug.LogWarning(
                    "Failed to initialize an GStreamer session due to missing " +
                    "executable file. Please check Gstreamer installation."
                );
            else if (!UnityEngine.SystemInfo.supportsAsyncGPUReadback)
                Debug.LogWarning(
                    "Failed to initialize an GStreamer session due to lack of " +
                    "async GPU readback support. Please try changing " +
                    "graphics API to readback-enabled one."
                );
            else
                _pipe = new GStreamerPipe(executable, arguments);
        }

        ~GStreamerSession()
        {
            if (_pipe != null)
                Debug.LogError(
                    "An unfinalized GStreamerCapture object was detected. " +
                    "It should be explicitly closed or disposed " +
                    "before being garbage-collected."
                );
        }

        #endregion

        #region Frame readback queue

        private List<AsyncGPUReadbackRequest> _readbackQueue =
            new List<AsyncGPUReadbackRequest>(4);

        private void QueueFrame(Texture source)
        {
            if (_readbackQueue.Count > 6)
            {
                Debug.LogWarning("Too many GPU readback requests.");
                return;
            }

            // Lazy initialization of the preprocessing blit shader
            if (_blitMaterial == null)
            {
                var shader = Shader.Find("Hidden/GStreamerOut/Preprocess");
                _blitMaterial = new Material(shader);
            }

            // Blit to a temporary texture and request readback on it.
            var rt = RenderTexture.GetTemporary
                (source.width, source.height, 0, RenderTextureFormat.ARGB32);
            Graphics.Blit(source, rt, _blitMaterial, 0);
            _readbackQueue.Add(AsyncGPUReadback.Request(rt));
            RenderTexture.ReleaseTemporary(rt);
        }

        private void ProcessQueue()
        {
            while (_readbackQueue.Count > 0)
            {
                // Check if the first entry in the queue is completed.
                if (!_readbackQueue[0].done)
                {
                    // Detect out-of-order case (the second entry in the queue
                    // is completed before the first entry).
                    if (_readbackQueue.Count > 1 && _readbackQueue[1].done)
                    {
                        // We can't allow the out-of-order case, so force it to
                        // be completed now.
                        _readbackQueue[0].WaitForCompletion();
                    }
                    else
                    {
                        // Nothing to do with the queue.
                        break;
                    }
                }

                // Retrieve the first entry in the queue.
                var req = _readbackQueue[0];
                _readbackQueue.RemoveAt(0);

                // Error detection
                if (req.hasError)
                {
                    Debug.LogWarning("GPU readback error was detected.");
                    continue;
                }

                // Feed the frame to the GStreamer pipe.
                _pipe.PushFrameData(req.GetData<byte>());
            }
        }

        #endregion
    }
}