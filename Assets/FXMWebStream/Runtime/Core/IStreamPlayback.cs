using System;
using UnityEngine;

namespace FXM.WebStream
{
    /// <summary>
    /// Playback control contract shared by the sender, the single receiver, and the
    /// multi-source receiver. UI (for example <see cref="StreamPlaybackControls"/>) only
    /// needs to talk through this interface.
    /// </summary>
    public interface IStreamPlaybackController
    {
        StreamPlaybackState PlaybackState { get; }

        /// <summary>True when the source supports seeking (for example VideoPlayer).</summary>
        bool CanSeek { get; }

        /// <summary>Playback position in seconds (0 for a live camera source).</summary>
        float PositionSeconds { get; }

        /// <summary>Duration in seconds (0 when unknown or live).</summary>
        float DurationSeconds { get; }

        void Play();
        void Pause();
        void Stop();
        void Restart();
        void Seek(float seconds);
    }

    /// <summary>
    /// Video source that <see cref="StreamingSender"/> can control.
    /// Built-in implementations: <see cref="VideoPlayerStreamSource"/> and
    /// <see cref="WebCamStreamSource"/>.
    /// </summary>
    public interface IStreamVideoSource
    {
        /// <summary>Texture currently filled by this video source.</summary>
        Texture SourceTexture { get; }

        StreamPlaybackState PlaybackState { get; }
        bool CanSeek { get; }
        float PositionSeconds { get; }
        float DurationSeconds { get; }

        void Play();
        void Pause();
        void Stop();
        void Restart();
        void Seek(float seconds);
    }
}
