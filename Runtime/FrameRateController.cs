using UnityEngine;
using System.Collections;

namespace GStreamerOut
{
    [AddComponentMenu("GStreamerOut/Frame Rate Controller")]
    public sealed class FrameRateController : MonoBehaviour
    {
        [SerializeField] private float _frameRate = 60;
        [SerializeField] private bool _offlineMode = true;

        private int _originalFrameRate;
        private int _originalVSyncCount;

        private int CalculateVSyncCount()
        {
            var rate = Screen.currentResolution.refreshRateRatio;
            var f_rate = (float)rate.numerator / (float)rate.denominator;

            // Return a positive value if it's divisible by the frame rate.
            if (Mathf.Approximately(f_rate % _frameRate, 0))
                return Mathf.RoundToInt(f_rate / _frameRate);
            else
                return 0; // Don't use v-sync.
        }

        private void OnEnable()
        {
            var ifps = Mathf.RoundToInt(_frameRate);

            if (_offlineMode)
            {
                _originalFrameRate = Time.captureFramerate;
                Time.captureFramerate = ifps;
            }
            else
            {
                _originalFrameRate = Application.targetFrameRate;
                _originalVSyncCount = QualitySettings.vSyncCount;
                Application.targetFrameRate = ifps;
                QualitySettings.vSyncCount = CalculateVSyncCount();
            }
        }

        private void OnDisable()
        {
            if (_offlineMode)
            {
                Time.captureFramerate = _originalFrameRate;
            }
            else
            {
                Application.targetFrameRate = _originalFrameRate;
                QualitySettings.vSyncCount = _originalVSyncCount;
            }
        }
    }
}