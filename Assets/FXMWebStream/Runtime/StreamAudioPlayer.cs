using System;
using UnityEngine;

namespace FXM.WebStream
{
    /// <summary>
    /// Plays PCM16 audio received by a <see cref="StreamReceiverCore"/>.
    ///
    /// Packets are queued in a small ring buffer and consumed by a streaming
    /// <see cref="AudioClip"/>, so playback stays smooth even when packets arrive in a
    /// burst. The clip is created for the sender's sample rate and channel count, which
    /// Unity resamples to the output device automatically.
    ///
    /// This component is added automatically by <see cref="StreamingReceiver"/> and
    /// <see cref="MultiStreamReceiver"/>; it can also be used directly.
    /// </summary>
    [AddComponentMenu("FXM WebStream/Stream Audio Player")]
    public sealed class StreamAudioPlayer : MonoBehaviour
    {
        [Tooltip("Playback volume for this source.")]
        [Range(0f, 1f)]
        public float volume = 1f;

        [Tooltip("Audio held before playback starts. Larger values survive more jitter but add latency.")]
        [Range(0.02f, 0.5f)]
        public float startBufferSeconds = 0.08f;

        private readonly object _lock = new object();
        private AudioSource _source;
        private AudioClip _clip;
        private byte[] _ring;
        private int _capacity;
        private int _readIndex;
        private int _writeIndex;
        private int _bufferedBytes;

        private int _sampleRate;
        private int _channels;
        private bool _started;
        private bool _playRequested = true;
        private bool _muted;

        public bool IsPlaying { get { return _started && _playRequested; } }
        public int SampleRate { get { return _sampleRate; } }
        public int Channels { get { return _channels; } }
        public int BufferedBytes { get { lock (_lock) { return _bufferedBytes; } } }

        private void Awake()
        {
            EnsureSource();
        }

        private void OnDestroy()
        {
            ReleaseClip();
        }

        /// <summary>Set the output volume (0..1).</summary>
        public void SetVolume(float value)
        {
            volume = Mathf.Clamp01(value);
            EnsureSource();
            _source.volume = volume;
        }

        /// <summary>Mute or unmute without stopping the stream (used by multi-source views).</summary>
        public void SetMuted(bool muted)
        {
            _muted = muted;
            EnsureSource();
            _source.mute = muted;
        }

        /// <summary>Queue one packet. Call from the main thread.</summary>
        public void Push(StreamAudioFrame frame)
        {
            if (frame.Pcm.Count <= 0 || frame.SampleRate <= 0 || frame.Channels <= 0)
            {
                return;
            }

            if (frame.SampleRate != _sampleRate || frame.Channels != _channels)
            {
                Reconfigure(frame.SampleRate, frame.Channels);
            }

            bool startNow = false;

            lock (_lock)
            {
                EnsureBuffer();

                int offset = frame.Pcm.Offset;
                int remaining = frame.Pcm.Count;
                while (remaining > 0)
                {
                    int space = _capacity - _bufferedBytes;
                    if (space <= 0)
                    {
                        // Drop the oldest audio; keeping up with live audio matters more
                        // than a glitch after a long stall.
                        int drop = Math.Min(_capacity / 4, _bufferedBytes);
                        _readIndex = (_readIndex + drop) % _capacity;
                        _bufferedBytes -= drop;
                        space = _capacity - _bufferedBytes;
                    }

                    int chunk = Math.Min(space, remaining);
                    int first = Math.Min(chunk, _capacity - _writeIndex);
                    Buffer.BlockCopy(frame.Pcm.Array, offset, _ring, _writeIndex, first);
                    if (first < chunk)
                    {
                        Buffer.BlockCopy(frame.Pcm.Array, offset + first, _ring, 0, chunk - first);
                    }

                    _writeIndex = (_writeIndex + chunk) % _capacity;
                    _bufferedBytes += chunk;
                    offset += chunk;
                    remaining -= chunk;
                }

                if (!_started && _playRequested)
                {
                    int frameBytes = Mathf.Max(1, _channels * 2);
                    int threshold = Mathf.Max(frameBytes, Mathf.RoundToInt(_sampleRate * startBufferSeconds) * frameBytes);
                    startNow = _bufferedBytes >= Mathf.Min(threshold, _capacity);
                }
            }

            if (startNow)
            {
                StartPlayback();
            }
        }

        /// <summary>Pause or resume playback. Pausing drops buffered audio and stops the clip.</summary>
        public void SetPlaying(bool playing)
        {
            bool wasRequested = _playRequested;
            _playRequested = playing;

            if (!playing)
            {
                StopPlayback(true);
            }
            else
            {
                if (!wasRequested)
                {
                    // Discard audio that arrived while paused so playback resumes live.
                    lock (_lock)
                    {
                        _readIndex = 0;
                        _writeIndex = 0;
                        _bufferedBytes = 0;
                    }
                }

                if (_started)
                {
                    EnsureSource();
                    if (!_source.isPlaying)
                    {
                        _source.Play();
                    }
                    else
                    {
                        _source.UnPause();
                    }
                }
            }
        }

        /// <summary>Stop playback and discard buffered audio.</summary>
        public void Clear()
        {
            StopPlayback(true);
        }

        private void EnsureSource()
        {
            if (_source != null)
            {
                return;
            }

            // Always use a dedicated source so an existing AudioSource on the same
            // GameObject keeps its own volume, loop, and mute settings.
            _source = gameObject.AddComponent<AudioSource>();

            _source.playOnAwake = false;
            _source.loop = true; // stream clips must loop while packets keep arriving
            _source.spatialBlend = 0f;
            _source.volume = volume;
            _source.mute = _muted;
        }

        private void Reconfigure(int sampleRate, int channels)
        {
            StopPlayback(true);

            lock (_lock)
            {
                _sampleRate = Mathf.Clamp(sampleRate, StreamAudioProtocol.MinSampleRate,
                    StreamAudioProtocol.MaxSampleRate);
                _channels = Mathf.Clamp(channels, 1, StreamAudioProtocol.MaxChannels);
                _ring = null;
                _capacity = 0;
                _readIndex = 0;
                _writeIndex = 0;
                _bufferedBytes = 0;
            }

            ReleaseClip();
        }

        private void EnsureBuffer()
        {
            if (_ring != null && _capacity > 0)
            {
                return;
            }

            int channels = Mathf.Clamp(_channels, 1, StreamAudioProtocol.MaxChannels);
            int rate = Mathf.Max(StreamAudioProtocol.MinSampleRate, _sampleRate);
            _capacity = rate * channels * 2 * 2; // two seconds
            _ring = new byte[_capacity];
            _readIndex = 0;
            _writeIndex = 0;
            _bufferedBytes = 0;
        }

        private void StartPlayback()
        {
            EnsureSource();

            if (_clip == null)
            {
                EnsureBuffer();
                string clipName = "FXM.Audio." + _sampleRate + "." + _channels;
                _clip = AudioClip.Create(clipName, Mathf.Max(1, _sampleRate), _channels, _sampleRate, true, OnPcmRead);
            }

            _source.clip = _clip;
            _source.volume = volume;
            _source.mute = _muted;
            _source.Play();
            _started = true;
        }

        private void StopPlayback(bool clearBuffer)
        {
            if (_source != null)
            {
                _source.Stop();
            }

            _started = false;

            if (!clearBuffer)
            {
                return;
            }

            lock (_lock)
            {
                _readIndex = 0;
                _writeIndex = 0;
                _bufferedBytes = 0;
            }
        }

        private void ReleaseClip()
        {
            if (_clip == null)
            {
                return;
            }

            if (Application.isPlaying)
            {
                Destroy(_clip);
            }
            else
            {
                DestroyImmediate(_clip);
            }

            _clip = null;

            if (_source != null)
            {
                _source.clip = null;
            }
        }

        /// <summary>Audio thread callback. Fills silence when the buffer is empty.</summary>
        private void OnPcmRead(float[] data)
        {
            int frameBytes = Mathf.Max(1, _channels * 2);

            lock (_lock)
            {
                if (_ring == null || !_playRequested)
                {
                    Array.Clear(data, 0, data.Length);
                    return;
                }

                int samples = data.Length;
                for (int i = 0; i < samples; i++)
                {
                    if (_bufferedBytes < 2)
                    {
                        data[i] = 0f;
                        continue;
                    }

                    int low = _ring[_readIndex];
                    _readIndex = (_readIndex + 1) % _capacity;
                    int high = _ring[_readIndex];
                    _readIndex = (_readIndex + 1) % _capacity;
                    _bufferedBytes -= 2;

                    short sample = (short)(low | (high << 8));
                    data[i] = sample / 32768f;
                }

                // Keep the ring aligned to whole frames if a packet was truncated.
                int remainder = _bufferedBytes % frameBytes;
                if (remainder != 0)
                {
                    _readIndex = (_readIndex + remainder) % _capacity;
                    _bufferedBytes -= remainder;
                }
            }
        }
    }
}
