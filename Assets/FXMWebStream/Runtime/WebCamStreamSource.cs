using System.Collections;
using UnityEngine;

namespace FXM.WebStream
{
    /// <summary>
    /// Live camera source backed by <see cref="WebCamTexture"/>.
    ///
    /// The webcam image is copied into a RenderTexture with a fixed size so
    /// <see cref="StreamingSender"/> can start before the camera finishes opening.
    /// Works on Windows, macOS, Android, and iOS. On Android and iOS the first use
    /// triggers the camera permission prompt.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("FXM WebStream/Webcam Stream Source")]
    public sealed class WebCamStreamSource : MonoBehaviour, IStreamVideoSource
    {
        [Header("Camera")]
        [Tooltip("Camera device name. Leave empty to use the first available camera (usually the back camera).")]
        public string deviceName = "";

        [Tooltip("Requested capture width. The source texture always has this size.")]
        public int requestedWidth = 1280;

        [Tooltip("Requested capture height. The source texture always has this size.")]
        public int requestedHeight = 720;

        [Tooltip("Requested capture frame rate.")]
        [Range(1, 60)]
        public int requestedFps = 30;

        [Tooltip("Mirror the camera image horizontally (useful for a front-facing camera).")]
        public bool mirrorHorizontally;

        [Tooltip("Flip the camera image vertically. Enable it when the received image is upside down.")]
        public bool flipVertically;

        [Tooltip("Start the camera automatically when the component becomes active.")]
        public bool playOnStart = true;

        [Header("Playback")]
        [Tooltip("Report the source as Playing when the camera opens.")]
        public bool autoPlay = true;

        /// <summary>Raised when the source state changes.</summary>
        public event System.Action<StreamPlaybackState> StateChanged;

        private WebCamTexture _webcam;
        private RenderTexture _target;
        private Coroutine _startRoutine;
        private StreamPlaybackState _state = StreamPlaybackState.Stopped;
        private bool _cameraReady;

        public Texture SourceTexture { get { return _target; } }
        public StreamPlaybackState PlaybackState { get { return _state; } }
        public bool CanSeek { get { return false; } }
        public float PositionSeconds { get { return 0f; } }
        public float DurationSeconds { get { return 0f; } }
        public WebCamTexture CameraTexture { get { return _webcam; } }
        public bool IsCameraReady { get { return _cameraReady; } }

        private void Awake()
        {
            requestedWidth = Mathf.Max(16, requestedWidth);
            requestedHeight = Mathf.Max(16, requestedHeight);
            EnsureTarget();
        }

        private void Start()
        {
            if (playOnStart)
            {
                Play();
            }
        }

        private void Update()
        {
            if (_webcam == null || !_webcam.isPlaying || _target == null)
            {
                return;
            }

            if (_webcam.didUpdateThisFrame)
            {
                var scale = new Vector2(mirrorHorizontally ? -1f : 1f, flipVertically ? -1f : 1f);
                var offset = new Vector2(mirrorHorizontally ? 1f : 0f, flipVertically ? 1f : 0f);
                Graphics.Blit(_webcam, _target, scale, offset);
            }
        }

        private void OnDestroy()
        {
            if (_startRoutine != null)
            {
                StopCoroutine(_startRoutine);
                _startRoutine = null;
            }

            if (_webcam != null)
            {
                if (_webcam.isPlaying)
                {
                    _webcam.Stop();
                }

                if (Application.isPlaying)
                {
                    Destroy(_webcam);
                }
                else
                {
                    DestroyImmediate(_webcam);
                }

                _webcam = null;
            }

            if (_target != null)
            {
                _target.Release();

                if (Application.isPlaying)
                {
                    Destroy(_target);
                }
                else
                {
                    DestroyImmediate(_target);
                }

                _target = null;
            }
        }

        public void Play()
        {
            if (_webcam == null)
            {
                if (_startRoutine == null && isActiveAndEnabled)
                {
                    _startRoutine = StartCoroutine(OpenCamera());
                }

                SetState(StreamPlaybackState.Playing);
                return;
            }

            if (!_webcam.isPlaying)
            {
                _webcam.Play();
            }

            SetState(StreamPlaybackState.Playing);
        }

        public void Pause()
        {
            if (_webcam != null && _webcam.isPlaying)
            {
                _webcam.Pause();
            }

            SetState(StreamPlaybackState.Paused);
        }

        public void Stop()
        {
            if (_webcam != null && _webcam.isPlaying)
            {
                _webcam.Stop();
            }

            SetState(StreamPlaybackState.Stopped);
        }

        public void Restart()
        {
            if (_webcam != null)
            {
                _webcam.Stop();
                _webcam.Play();
                SetState(StreamPlaybackState.Playing);
                return;
            }

            Play();
        }

        public void Seek(float seconds)
        {
            // Live camera has no timeline.
        }

        private IEnumerator OpenCamera()
        {
#if UNITY_ANDROID || UNITY_IOS
            yield return Application.RequestUserAuthorization(UserAuthorization.WebCam);

            if (!Application.HasUserAuthorization(UserAuthorization.WebCam))
            {
                Debug.LogWarning("[FXM.WebStream] Camera permission was not granted.");
                SetState(StreamPlaybackState.Stopped);
                _startRoutine = null;
                yield break;
            }
#else
            yield return null;
#endif

            string selectedDevice = deviceName;
            if (string.IsNullOrEmpty(selectedDevice) && WebCamTexture.devices.Length > 0)
            {
                selectedDevice = WebCamTexture.devices[0].name;
            }

            if (string.IsNullOrEmpty(selectedDevice))
            {
                Debug.LogWarning("[FXM.WebStream] No camera device found.");
                SetState(StreamPlaybackState.Stopped);
                _startRoutine = null;
                yield break;
            }

            _webcam = new WebCamTexture(selectedDevice, requestedWidth, requestedHeight, requestedFps);
            _webcam.Play();

            float timeout = Time.realtimeSinceStartup + 5f;
            while (_webcam != null && !_webcam.isPlaying && Time.realtimeSinceStartup < timeout)
            {
                yield return null;
            }

            _cameraReady = _webcam != null && _webcam.isPlaying;
            if (!_cameraReady)
            {
                Debug.LogWarning("[FXM.WebStream] Camera did not start: " + selectedDevice);
                SetState(StreamPlaybackState.Stopped);
                _startRoutine = null;
                yield break;
            }

            if (!autoPlay)
            {
                _webcam.Pause();
                SetState(StreamPlaybackState.Paused);
            }
            else
            {
                SetState(StreamPlaybackState.Playing);
            }

            _startRoutine = null;
        }

        private void EnsureTarget()
        {
            if (_target != null &&
                _target.width == requestedWidth &&
                _target.height == requestedHeight)
            {
                return;
            }

            if (_target != null)
            {
                _target.Release();
                Destroy(_target);
            }

            _target = new RenderTexture(requestedWidth, requestedHeight, 0, RenderTextureFormat.ARGB32)
            {
                name = "FXM.WebCamSource",
                useMipMap = false,
                autoGenerateMips = false,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
            _target.Create();

            var previous = RenderTexture.active;
            RenderTexture.active = _target;
            GL.Clear(true, true, Color.black);
            RenderTexture.active = previous;
        }

        private void OnValidate()
        {
            requestedWidth = Mathf.Max(16, requestedWidth);
            requestedHeight = Mathf.Max(16, requestedHeight);
        }

        private void SetState(StreamPlaybackState state)
        {
            if (_state == state)
            {
                return;
            }

            _state = state;
            var handler = StateChanged;
            if (handler != null)
            {
                handler(state);
            }
        }
    }
}
