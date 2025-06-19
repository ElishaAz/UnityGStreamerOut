using UnityEngine;
using System.Collections;

namespace GStreamerOut
{
    [AddComponentMenu("GStreamerOut/Camera Capture")]
    public sealed class CameraCapture : MonoBehaviour
    {
        #region Public properties

        [SerializeField] private string _executable = "gst-launch-1.0";

        [SerializeField] private int _width = 1920;

        [SerializeField] private int _height = 1080;

        [SerializeField] private float _frameRate = 60;

        [SerializeField] private string _pipeline = "videoconvert ! autovideosink";

        [SerializeField] private bool createBlitter = true;
        [SerializeField] private bool preprocess = true;

        #endregion

        #region Private members

        private GStreamerSession _session;
        private RenderTexture _tempRT;
        private GameObject _blitter;

        private Camera camera;

        private RenderTextureFormat GetTargetFormat()
        {
            if (!preprocess)
            {
                return RenderTextureFormat.ARGB32;
            }

            return camera.allowHDR ? RenderTextureFormat.DefaultHDR : RenderTextureFormat.Default;
        }

        private int GetAntiAliasingLevel()
        {
            return Mathf.Max(camera.allowMSAA ? QualitySettings.antiAliasing : 1, 1);
        }

        #endregion

        #region Time-keeping variables

        private int _frameCount;
        private float _startTime;
        private int _frameDropCount;

        private float FrameTime => _startTime + (_frameCount - 0.5f) / _frameRate;

        private void WarnFrameDrop()
        {
            if (++_frameDropCount != 10) return;

            Debug.LogWarning(
                "Significant frame dropping was detected. This may introduce " +
                "time instability into output video. Decreasing the recording " +
                "frame rate is recommended."
            );
        }

        #endregion

        #region MonoBehaviour implementation

        private void OnDisable()
        {
            if (_session != null)
            {
                // Close and dispose the GStreamer session.
                _session.Close();
                _session.Dispose();
                _session = null;
            }

            if (_tempRT != null)
            {
                // Dispose the frame texture.
                GetComponent<Camera>().targetTexture = null;
                Destroy(_tempRT);
                _tempRT = null;
            }

            if (_blitter != null)
            {
                // Destroy the blitter game object.
                Destroy(_blitter);
                _blitter = null;
            }
        }

        private void Awake()
        {
            camera = GetComponent<Camera>();
        }

        private IEnumerator Start()
        {
            // Sync with GStreamer pipe thread at the end of every frame.
            for (var eof = new WaitForEndOfFrame();;)
            {
                yield return eof;
                _session?.CompletePushFrames();
            }
        }

        private void Update()
        {
            // Lazy initialization
            if (_session == null)
            {
                // Give a newly created temporary render texture to the camera
                // if it's set to render to a screen. Also create a blitter
                // object to keep frames presented on the screen.
                if (camera.targetTexture == null)
                {
                    _tempRT = new RenderTexture(_width, _height, 24, GetTargetFormat());
                    _tempRT.antiAliasing = GetAntiAliasingLevel();
                    camera.targetTexture = _tempRT;
                    if (createBlitter)
                        _blitter = Blitter.CreateInstance(camera);
                }

                // Start a GStreamer session.
                _session = GStreamerSession.Create(
                    _executable,
                    camera.targetTexture.width,
                    camera.targetTexture.height,
                    _frameRate, _pipeline, preprocess
                );

                _startTime = Time.time;
                _frameCount = 0;
                _frameDropCount = 0;
            }

            var gap = Time.time - FrameTime;
            var delta = 1 / _frameRate;

            if (gap < 0)
            {
                // Update without frame data.
                _session.PushFrame(null);
            }
            else if (gap < delta)
            {
                // Single-frame behind from the current time:
                // Push the current frame to GStreamer.
                _session.PushFrame(camera.targetTexture);
                _frameCount++;
            }
            else if (gap < delta * 2)
            {
                // Two-frame behind from the current time:
                // Push the current frame twice to GStreamer. Actually this is not
                // an efficient way to catch up. We should think about
                // implementing frame duplication in a more proper way. #fixme
                _session.PushFrame(camera.targetTexture);
                _session.PushFrame(camera.targetTexture);
                _frameCount += 2;
            }
            else
            {
                // Show a warning message about the situation.
                WarnFrameDrop();

                // Push the current frame to GStreamer.
                _session.PushFrame(camera.targetTexture);

                // Compensate the time delay.
                _frameCount += Mathf.FloorToInt(gap * _frameRate);
            }
        }

        #endregion
    }
}