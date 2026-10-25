using System;
using UnityEngine;
using UnityEngine.UI;

namespace FXM.WebStream
{
    /// <summary>
    /// Playback controls for a stream (play, pause, stop, restart) plus a seek slider
    /// and status/time labels.
    ///
    /// The component does not care whether the target is <see cref="StreamingSender"/>,
    /// <see cref="StreamingReceiver"/>, or <see cref="MultiStreamReceiver"/>: all of them
    /// implement <see cref="IStreamPlaybackController"/>.
    ///
    /// Every button is optional; only assigned controls are used. The slider disables
    /// itself when the source cannot seek or its duration is unknown (for example a live
    /// camera).
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("FXM WebStream/Stream Playback Controls")]
    public sealed class StreamPlaybackControls : MonoBehaviour
    {
        [Tooltip("Target component: StreamingSender, StreamingReceiver, or MultiStreamReceiver.")]
        public MonoBehaviour controllerBehaviour;

        [Header("Buttons (optional)")]
        public Button playButton;
        public Button pauseButton;
        public Button stopButton;
        public Button restartButton;

        [Header("Seek (optional)")]
        public Slider seekSlider;

        [Header("Labels (optional)")]
        public Text statusLabel;
        public Text timeLabel;

        [Header("Behavior")]
        [Tooltip("Hide the slider when the duration is unknown or seeking is unsupported.")]
        public bool hideSeekWhenUnsupported = true;

        private IStreamPlaybackController _controller;
        private bool _updatingSlider;

        private void Awake()
        {
            ResolveController();
        }

        private void OnEnable()
        {
            ResolveController();
            Wire(true);
            UpdateUi();
        }

        private void OnDisable()
        {
            Wire(false);
        }

        private void Update()
        {
            if (_controller == null || !isActiveAndEnabled)
            {
                return;
            }

            UpdateUi();
        }

        /// <summary>Change the control target at runtime.</summary>
        public void SetController(IStreamPlaybackController controller)
        {
            Wire(false);
            _controller = controller;
            controllerBehaviour = controller as MonoBehaviour;
            Wire(true);
            UpdateUi();
        }

        private void ResolveController()
        {
            if (_controller != null)
            {
                return;
            }

            _controller = controllerBehaviour as IStreamPlaybackController;

            if (_controller == null && controllerBehaviour == null)
            {
                // Fallback: look for a matching component on this GameObject.
                var behaviours = GetComponents<MonoBehaviour>();
                for (int i = 0; i < behaviours.Length; i++)
                {
                    var candidate = behaviours[i] as IStreamPlaybackController;
                    if (candidate != null)
                    {
                        _controller = candidate;
                        controllerBehaviour = behaviours[i];
                        break;
                    }
                }
            }

            if (_controller == null && controllerBehaviour != null)
            {
                Debug.LogWarning("[FXM.WebStream] controllerBehaviour does not implement IStreamPlaybackController.");
            }
        }

        private void Wire(bool add)
        {
            if (playButton != null)
            {
                if (add) playButton.onClick.AddListener(OnPlay);
                else playButton.onClick.RemoveListener(OnPlay);
            }

            if (pauseButton != null)
            {
                if (add) pauseButton.onClick.AddListener(OnPause);
                else pauseButton.onClick.RemoveListener(OnPause);
            }

            if (stopButton != null)
            {
                if (add) stopButton.onClick.AddListener(OnStop);
                else stopButton.onClick.RemoveListener(OnStop);
            }

            if (restartButton != null)
            {
                if (add) restartButton.onClick.AddListener(OnRestart);
                else restartButton.onClick.RemoveListener(OnRestart);
            }

            if (seekSlider != null)
            {
                if (add) seekSlider.onValueChanged.AddListener(OnSeekChanged);
                else seekSlider.onValueChanged.RemoveListener(OnSeekChanged);
            }
        }

        private void OnPlay() { if (_controller != null) _controller.Play(); }
        private void OnPause() { if (_controller != null) _controller.Pause(); }
        private void OnStop() { if (_controller != null) _controller.Stop(); }
        private void OnRestart() { if (_controller != null) _controller.Restart(); }

        private void OnSeekChanged(float value)
        {
            if (_updatingSlider || _controller == null || !_controller.CanSeek)
            {
                return;
            }

            float duration = _controller.DurationSeconds;
            if (duration <= 0.01f)
            {
                return;
            }

            _controller.Seek(value * duration);
        }

        private void UpdateUi()
        {
            if (_controller == null)
            {
                return;
            }

            StreamPlaybackState state = _controller.PlaybackState;

            if (playButton != null)
            {
                playButton.interactable = state != StreamPlaybackState.Playing;
            }

            if (pauseButton != null)
            {
                pauseButton.interactable = state == StreamPlaybackState.Playing;
            }

            if (statusLabel != null)
            {
                statusLabel.text = state == StreamPlaybackState.Playing
                    ? "Streaming"
                    : (state == StreamPlaybackState.Paused ? "Paused" : "Stopped");
            }

            float duration = _controller.DurationSeconds;
            bool canSeek = _controller.CanSeek && duration > 0.01f;

            if (timeLabel != null)
            {
                if (duration > 0.01f)
                {
                    timeLabel.text = FormatTime(_controller.PositionSeconds) + " / " + FormatTime(duration);
                }
                else
                {
                    timeLabel.text = FormatTime(_controller.PositionSeconds) + " / LIVE";
                }
            }

            if (seekSlider != null)
            {
                if (hideSeekWhenUnsupported)
                {
                    seekSlider.gameObject.SetActive(canSeek);
                }
                else
                {
                    seekSlider.interactable = canSeek;
                }

                if (canSeek && !seekSlider.IsPressed())
                {
                    _updatingSlider = true;
                    seekSlider.minValue = 0f;
                    seekSlider.maxValue = 1f;
                    seekSlider.value = Mathf.Clamp01(_controller.PositionSeconds / duration);
                    _updatingSlider = false;
                }
            }
        }

        private static string FormatTime(float seconds)
        {
            if (seconds < 0f || float.IsNaN(seconds))
            {
                seconds = 0f;
            }

            int total = Mathf.FloorToInt(seconds);
            int minutes = total / 60;
            int secs = total % 60;
            return minutes.ToString("00") + ":" + secs.ToString("00");
        }
    }
}
