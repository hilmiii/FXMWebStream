using System;
using UnityEngine;
using UnityEngine.Video;

namespace FXM.WebStream
{
    /// <summary>
    /// Turns a <see cref="VideoPlayer"/> into a streaming source.
    ///
    /// The component creates the target RenderTexture, plays video into it, and forwards
    /// play/pause/stop/restart/seek commands from receivers to the VideoPlayer. Add it to
    /// the same GameObject as <see cref="StreamingSender"/> (the sender finds it
    /// automatically) or assign StreamingSender.videoSourceBehaviour.
    ///
    /// Notes:
    /// - To stream the clip's audio, the sound must pass through an AudioSource so the
    ///   scene AudioListener can capture it. This component routes the first audio track
    ///   to an AudioSource by default.
    /// - Seek uses seconds and only applies to seekable sources (local VideoClip assets
    ///   usually work; some live streams do not).
    /// </summary>
    [RequireComponent(typeof(VideoPlayer))]
    [AddComponentMenu("FXM WebStream/Video Player Stream Source")]
    public sealed class VideoPlayerStreamSource : MonoBehaviour, IStreamVideoSource
    {
        [Header("Video Source")]
        [Tooltip("Leave empty to use the VideoPlayer on this GameObject.")]
        public VideoPlayer videoPlayer;

        [Tooltip("Local VideoClip asset. Takes priority over URL.")]
        public VideoClip clip;

        [Tooltip("Video URL (local file, HTTP mp4, or HLS) used when Clip is empty.")]
        public string url;

        [Header("Render Texture")]
        [Tooltip("Video render resolution before StreamingSender downscales it.")]
        public int width = 1280;

        public int height = 720;

        [Tooltip("Leave empty to create one automatically from width/height.")]
        public RenderTexture targetTexture;

        [Header("Playback")]
        public bool playOnStart = true;
        public bool loop = true;

        [Header("Audio")]
        [Tooltip("Route the video's first audio track through an AudioSource so it can be captured for streaming.")]
        public bool routeAudioToAudioSource = true;

        [Tooltip("Start playback at this time in seconds (0 = beginning).")]
        public float startTimeSeconds;

        /// <summary>State change raised by this component (for example when the video ends).</summary>
        public event Action<StreamPlaybackState> StateChanged;

        private StreamPlaybackState _state = StreamPlaybackState.Stopped;
        private AudioSource _audioSource;
        private bool _ownsTexture;
        private bool _configured;
        private bool _startTimeApplied;

        public Texture SourceTexture { get { return targetTexture; } }
        public StreamPlaybackState PlaybackState { get { return _state; } }
        public bool CanSeek { get { return true; } }

        public float PositionSeconds
        {
            get
            {
                if (videoPlayer == null)
                {
                    return 0f;
                }

                // time can be negative briefly after Stop on some platforms.
                return Mathf.Max(0f, (float)videoPlayer.time);
            }
        }

        public float DurationSeconds
        {
            get
            {
                if (videoPlayer == null)
                {
                    return 0f;
                }

                if (videoPlayer.clip != null && videoPlayer.clip.length > 0.01f)
                {
                    return (float)videoPlayer.clip.length;
                }

                // For URLs, length is only valid after preparation completes.
                return videoPlayer.isPrepared ? (float)videoPlayer.length : 0f;
            }
        }

        private void Reset()
        {
            videoPlayer = GetComponent<VideoPlayer>();
        }

        private void Awake()
        {
            if (videoPlayer == null)
            {
                videoPlayer = GetComponent<VideoPlayer>();
            }

            if (videoPlayer == null)
            {
                enabled = false;
                UnityEngine.Debug.LogError("[FXM.WebStream] VideoPlayerStreamSource requires a VideoPlayer component.");
                return;
            }

            // Playback is driven through the API, not automatically by Unity.
            videoPlayer.playOnAwake = false;
            videoPlayer.isLooping = loop;
            videoPlayer.prepareCompleted += OnPrepareCompleted;
            videoPlayer.loopPointReached += OnLoopPointReached;

            // The RenderTexture is created in Awake so StreamingSender (whose Start may run
            // first) can already read SourceTexture.
            EnsureRenderTexture();
            ConfigureSource();
        }

        private void Start()
        {
            if (playOnStart)
            {
                Play();
            }
        }

        private void OnDestroy()
        {
            if (videoPlayer != null)
            {
                videoPlayer.prepareCompleted -= OnPrepareCompleted;
                videoPlayer.loopPointReached -= OnLoopPointReached;
            }

            if (videoPlayer != null && targetTexture != null && videoPlayer.targetTexture == targetTexture)
            {
                videoPlayer.targetTexture = null;
            }

            if (_ownsTexture && targetTexture != null)
            {
                targetTexture.Release();
                if (Application.isPlaying)
                {
                    Destroy(targetTexture);
                }
                else
                {
                    DestroyImmediate(targetTexture);
                }
            }

            targetTexture = null;
        }


        // ------------------------------------------------------------------
        // IStreamVideoSource
        // ------------------------------------------------------------------

        public void Play()
        {
            if (videoPlayer == null)
            {
                return;
            }

            videoPlayer.Play();
            SetState(StreamPlaybackState.Playing);
        }

        public void Pause()
        {
            if (videoPlayer == null)
            {
                return;
            }

            videoPlayer.Pause();
            SetState(StreamPlaybackState.Paused);
        }

        public void Stop()
        {
            if (videoPlayer == null)
            {
                return;
            }

            videoPlayer.Stop();
            SetState(StreamPlaybackState.Stopped);
        }

        public void Restart()
        {
            if (videoPlayer == null)
            {
                return;
            }

            // An explicit Restart wins over startTimeSeconds, which prepareCompleted may not
            // have applied yet.
            _startTimeApplied = true;
            videoPlayer.time = 0d;
            videoPlayer.Play();
            SetState(StreamPlaybackState.Playing);
        }

        public void Seek(float seconds)
        {
            if (videoPlayer == null)
            {
                return;
            }

            float duration = DurationSeconds;
            float target = Mathf.Max(0f, seconds);
            if (duration > 0.01f)
            {
                target = Mathf.Min(target, duration - 0.01f);
            }

            videoPlayer.time = target;
        }

        // ------------------------------------------------------------------
        // Setup
        // ------------------------------------------------------------------

        private void EnsureRenderTexture()
        {
            if (targetTexture == null)
            {
                targetTexture = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
                {
                    name = "FXM.VideoSource",
                    useMipMap = false,
                    autoGenerateMips = false,
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp
                };

                targetTexture.Create();
                _ownsTexture = true;
            }

            videoPlayer.renderMode = VideoRenderMode.RenderTexture;
            videoPlayer.targetTexture = targetTexture;
        }

        /// <summary>
        /// Assign a VideoClip at runtime and reload the source. Use this instead of
        /// writing <see cref="clip"/> directly after Awake, which would not reconfigure
        /// the underlying VideoPlayer.
        /// </summary>
        public void SetClip(VideoClip newClip)
        {
            clip = newClip;
            url = null;
            ApplySource(true);
        }

        /// <summary>
        /// Assign a URL at runtime. Local absolute paths are converted to file:// URLs.
        /// </summary>
        public void SetUrl(string newUrl)
        {
            clip = null;
            url = NormalizeUrl(newUrl);
            ApplySource(true);
        }

        /// <summary>Re-apply the current clip/url to the VideoPlayer.</summary>
        [ContextMenu("Reload Video Source")]
        public void Reload()
        {
            ApplySource(true);
        }

        private void ConfigureSource()
        {
            ApplySource(false);
        }

        private void ApplySource(bool force)
        {
            if (videoPlayer == null)
            {
                return;
            }

            if (_configured && !force)
            {
                return;
            }

            ConfigureAudioOutput();
            videoPlayer.Stop();
            _startTimeApplied = false;

            if (clip != null)
            {
                videoPlayer.source = VideoSource.VideoClip;
                videoPlayer.clip = clip;
                videoPlayer.url = null;
            }
            else if (!string.IsNullOrEmpty(url))
            {
                videoPlayer.source = VideoSource.Url;
                videoPlayer.clip = null;
                videoPlayer.url = NormalizeUrl(url);
            }
            else
            {
                UnityEngine.Debug.LogWarning("[FXM.WebStream] VideoPlayerStreamSource has no Clip or URL assigned.");
            }

            videoPlayer.isLooping = loop;
            _configured = true;

            // Reapply after the source changes, which can reset the audio output mode.
            ConfigureAudioOutput();
        }

        private void ConfigureAudioOutput()
        {
            if (!routeAudioToAudioSource || videoPlayer == null)
            {
                return;
            }

            if (_audioSource == null)
            {
                _audioSource = GetComponent<AudioSource>();
                if (_audioSource == null)
                {
                    _audioSource = gameObject.AddComponent<AudioSource>();
                }
            }

            _audioSource.playOnAwake = false;
            _audioSource.loop = false;
            _audioSource.spatialBlend = 0f;

            videoPlayer.audioOutputMode = VideoAudioOutputMode.AudioSource;
            videoPlayer.controlledAudioTrackCount = 1;
            videoPlayer.EnableAudioTrack(0, true);
            videoPlayer.SetTargetAudioSource(0, _audioSource);
        }

        /// <summary>
        /// Convert a local absolute path to a file:// URL. HTTP(S), rtsp, and file URLs
        /// are returned unchanged.
        /// </summary>
        public static string NormalizeUrl(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return value;
            }

            string trimmed = value.Trim();
            if (trimmed.IndexOf("://", StringComparison.Ordinal) >= 0)
            {
                return trimmed;
            }

            if (trimmed.StartsWith("/", StringComparison.Ordinal))
            {
                return "file://" + trimmed;
            }

            if (trimmed.Length > 1 && trimmed[1] == ':')
            {
                return "file:///" + trimmed.Replace('\\', '/');
            }

            return trimmed;
        }

        private void OnValidate()
        {
            width = Mathf.Max(16, width);
            height = Mathf.Max(16, height);
            url = NormalizeUrl(url);
        }

        private void OnPrepareCompleted(VideoPlayer source)
        {
            if (_startTimeApplied || startTimeSeconds <= 0.01f)
            {
                return;
            }

            // time is only valid after preparation completes.
            source.time = startTimeSeconds;
            _startTimeApplied = true;
        }

        private void OnLoopPointReached(VideoPlayer source)
        {
            // VideoPlayer.isLooping handles repetition; notify the UI when playback stops.
            if (!loop)
            {
                SetState(StreamPlaybackState.Stopped);
            }
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
