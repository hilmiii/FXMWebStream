using System;
using UnityEngine;

namespace FXM.WebStream
{
    /// <summary>
    /// Captures the final audio mix of the scene and turns it into PCM16 packets.
    ///
    /// Unity calls <see cref="OnAudioFilterRead"/> on components attached to the same
    /// GameObject as an <see cref="AudioListener"/>. Add this component there (the sender
    /// does it automatically) and enable <see cref="Capturing"/> while streaming.
    ///
    /// The mix is downmixed to mono or stereo and resampled on the audio thread, so the
    /// main thread only copies finished packets. Nothing is allocated per audio callback.
    /// WebGL has no audio thread, so audio capture is unavailable there.
    /// </summary>
    [AddComponentMenu("FXM WebStream/Stream Audio Capture")]
    public sealed class StreamAudioCapture : MonoBehaviour
    {
        [Tooltip("Packets per second target. 24000 is a good balance for speech and music.")]
        [Range(StreamAudioProtocol.MinSampleRate, StreamAudioProtocol.MaxSampleRate)]
        public int targetSampleRate = 24000;

        [Tooltip("1 = mono (half the bandwidth), 2 = stereo.")]
        [Range(1, StreamAudioProtocol.MaxChannels)]
        public int targetChannels = 1;

        [Tooltip("Capture gain. Values above 1 can clip.")]
        [Range(0f, 2f)]
        public float gain = 1f;

        [Tooltip("Audio packet duration. 0.04 seconds keeps latency low with little overhead.")]
        [Range(0.02f, 0.1f)]
        public float packetSeconds = 0.04f;

        private readonly object _lock = new object();
        private byte[] _ring;
        private int _capacity;
        private int _readIndex;
        private int _writeIndex;
        private int _bufferedBytes;

        private int _sourceRate = 48000;
        private double _step = 2.0;
        private double _nextOutputTime;
        private long _frameIndex;
        private bool _hasFrame;
        private float _previousLeft;
        private float _previousRight;

        /// <summary>True while samples are collected. Set by the sender.</summary>
        public bool Capturing { get; set; }

        /// <summary>Bytes in one packet (PCM only, without the protocol header).</summary>
        public int PacketBytes
        {
            get
            {
                int frameSamples = Mathf.Max(1, Mathf.RoundToInt(targetSampleRate * packetSeconds));
                return frameSamples * Mathf.Clamp(targetChannels, 1, StreamAudioProtocol.MaxChannels) * 2;
            }
        }

        public int BufferedBytes
        {
            get
            {
                lock (_lock)
                {
                    return _bufferedBytes;
                }
            }
        }

        private void OnDisable()
        {
            Capturing = false;
            Clear();
        }

        /// <summary>Change the output format. Clears any buffered audio.</summary>
        public void Configure(int sampleRate, int channels, float captureGain)
        {
            lock (_lock)
            {
                targetSampleRate = Mathf.Clamp(sampleRate, StreamAudioProtocol.MinSampleRate,
                    StreamAudioProtocol.MaxSampleRate);
                targetChannels = Mathf.Clamp(channels, 1, StreamAudioProtocol.MaxChannels);
                gain = Mathf.Clamp(captureGain, 0f, 2f);
                _sourceRate = Mathf.Max(1, AudioSettings.outputSampleRate);
                _step = _sourceRate / (double)targetSampleRate;
                EnsureBuffer();
                ResetResampler();
                _readIndex = 0;
                _writeIndex = 0;
                _bufferedBytes = 0;
            }
        }

        /// <summary>Discard buffered audio and restart the resampler.</summary>
        public void Clear()
        {
            lock (_lock)
            {
                _readIndex = 0;
                _writeIndex = 0;
                _bufferedBytes = 0;
                ResetResampler();
            }
        }

        /// <summary>
        /// Copy exactly <see cref="PacketBytes"/> bytes when enough audio is buffered.
        /// Returns false while the buffer is still filling.
        /// </summary>
        public bool TryDequeuePacket(byte[] destination, int offset, out int length)
        {
            length = 0;

            if (destination == null)
            {
                return false;
            }

            int packetBytes = PacketBytes;
            if (packetBytes <= 0 || destination.Length - offset < packetBytes)
            {
                return false;
            }

            lock (_lock)
            {
                if (_ring == null || _bufferedBytes < packetBytes)
                {
                    return false;
                }

                int first = Math.Min(packetBytes, _capacity - _readIndex);
                Buffer.BlockCopy(_ring, _readIndex, destination, offset, first);
                if (first < packetBytes)
                {
                    Buffer.BlockCopy(_ring, 0, destination, offset + first, packetBytes - first);
                }

                _readIndex = (_readIndex + packetBytes) % _capacity;
                _bufferedBytes -= packetBytes;
                length = packetBytes;
                return true;
            }
        }

        /// <summary>Called by Unity on the audio thread with the final mix.</summary>
        private void OnAudioFilterRead(float[] data, int channels)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            return;
#else
            if (!Capturing || data == null || channels <= 0)
            {
                return;
            }

            int frames = data.Length / channels;
            if (frames <= 0)
            {
                return;
            }

            lock (_lock)
            {
                if (_ring == null)
                {
                    _step = _sourceRate / (double)targetSampleRate;
                    EnsureBuffer();
                }

                for (int i = 0; i < frames; i++)
                {
                    int baseIndex = i * channels;
                    float left;
                    float right;

                    if (channels == 1)
                    {
                        left = data[baseIndex];
                        right = left;
                    }
                    else
                    {
                        left = data[baseIndex];
                        right = data[baseIndex + 1];

                        if (channels > 2)
                        {
                            // Collapse surround to mono so no channel is silently lost.
                            float sum = 0f;
                            for (int channel = 0; channel < channels; channel++)
                            {
                                sum += data[baseIndex + channel];
                            }

                            left = sum / channels;
                            right = left;
                        }
                    }

                    left = Mathf.Clamp(left * gain, -1f, 1f);
                    right = Mathf.Clamp(right * gain, -1f, 1f);

                    long frame = _frameIndex++;

                    if (!_hasFrame)
                    {
                        _hasFrame = true;
                        _nextOutputTime = 0;
                    }

                    // Emit every output sample whose time falls inside [frame-1, frame].
                    while (_nextOutputTime <= frame)
                    {
                        float fraction = frame == 0
                            ? 0f
                            : Mathf.Clamp01((float)(_nextOutputTime - (frame - 1)));
                        float outLeft = frame == 0 ? left : Mathf.Lerp(_previousLeft, left, fraction);
                        float outRight = frame == 0 ? right : Mathf.Lerp(_previousRight, right, fraction);
                        WriteSample(outLeft, outRight);
                        _nextOutputTime += _step;
                    }

                    _previousLeft = left;
                    _previousRight = right;
                }
            }
#endif
        }

        private void EnsureBuffer()
        {
            int channels = Mathf.Clamp(targetChannels, 1, StreamAudioProtocol.MaxChannels);
            int desired = Mathf.Max(PacketBytes * 4, targetSampleRate * channels * 2 * 2);
            if (_ring != null && _capacity >= desired)
            {
                return;
            }

            _ring = new byte[desired];
            _capacity = desired;
            _readIndex = 0;
            _writeIndex = 0;
            _bufferedBytes = 0;
        }

        private void ResetResampler()
        {
            _frameIndex = 0;
            _nextOutputTime = 0;
            _hasFrame = false;
            _previousLeft = 0f;
            _previousRight = 0f;
        }

        private void WriteSample(float left, float right)
        {
            if (_bufferedBytes + targetChannels * 2 > _capacity)
            {
                // Drop the oldest sample so latency stays bounded.
                _readIndex = (_readIndex + targetChannels * 2) % _capacity;
                _bufferedBytes -= targetChannels * 2;
            }

            if (targetChannels == 1)
            {
                WriteInt16((left + right) * 0.5f);
            }
            else
            {
                WriteInt16(left);
                WriteInt16(right);
            }
        }

        private void WriteInt16(float value)
        {
            int sample = Mathf.RoundToInt(Mathf.Clamp(value, -1f, 1f) * 32767f);
            _ring[_writeIndex] = (byte)(sample & 0xFF);
            _writeIndex = (_writeIndex + 1) % _capacity;
            _ring[_writeIndex] = (byte)((sample >> 8) & 0xFF);
            _writeIndex = (_writeIndex + 1) % _capacity;
            _bufferedBytes += 2;
        }
    }
}
