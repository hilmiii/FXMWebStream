using System;
using System.Diagnostics;
using UnityEngine;

namespace FXM.WebStream
{
    /// <summary>
    /// Receiver core: moves frames from the transport, decodes JPEG into ONE reused
    /// Texture2D, and forwards control messages to its owner.
    ///
    /// This class does not touch UI. <see cref="StreamingReceiver"/> uses it for a single
    /// source, while <see cref="MultiStreamReceiver"/> creates one core per source
    /// (many-to-one / many-to-many).
    ///
    /// All callbacks (FrameDecoded, ControlReceived) run on the main thread inside
    /// <see cref="Poll"/> or from transport events raised by Poll.
    /// </summary>
    public sealed class StreamReceiverCore : IDisposable
    {
        private readonly INetworkTransport _transport;
        private readonly FrameBufferPool _decodePool = new FrameBufferPool(2);
        private readonly bool _markTextureNonReadable;

        private Texture2D _texture;
        private bool _attached;
        private bool _disposed;
        private float _nextDecodeTime;
        private bool _loggedDecodeError;

        private int _maxDecodeFps;
        private int _decodedFrames;
        private int _skippedFrames;
        private int _decodeErrors;
        private int _decodedInWindow;
        private float _statAccumulator;
        private float _receiveFps;
        private double _lastDecodeMilliseconds;
        private int _lastBytes;
        private int _textureWidth;
        private int _textureHeight;

        public StreamReceiverCore(INetworkTransport transport, int maxDecodeFps, bool markTextureNonReadable)
        {
            if (transport == null)
            {
                throw new ArgumentNullException("transport");
            }

            _transport = transport;
            _maxDecodeFps = Mathf.Max(1, maxDecodeFps);
            _markTextureNonReadable = markTextureNonReadable;
        }

        /// <summary>Raised after each successful decode; the texture already holds the latest frame.</summary>
        public event Action<Texture2D> FrameDecoded;

        /// <summary>Control message (state from a sender or a command from another peer).</summary>
        public event Action<StreamControlMessage> ControlReceived;

        /// <summary>An audio packet arrived. The PCM buffer is only valid during the callback.</summary>
        public event Action<StreamAudioFrame> AudioReceived;

        public INetworkTransport Transport { get { return _transport; } }
        public Texture2D Texture { get { return _texture; } }

        /// <summary>When false, incoming frames are not decoded (for example while paused).</summary>
        public bool AcceptFrames = true;

        public int MaxDecodeFps
        {
            get { return _maxDecodeFps; }
            set { _maxDecodeFps = Mathf.Max(1, value); }
        }

        public int DecodedFrames { get { return _decodedFrames; } }
        public int SkippedFrames { get { return _skippedFrames; } }
        public int DecodeErrors { get { return _decodeErrors; } }
        public float ReceiveFps { get { return _receiveFps; } }
        public double LastDecodeMilliseconds { get { return _lastDecodeMilliseconds; } }
        public int LastBytes { get { return _lastBytes; } }
        public int TextureWidth { get { return _textureWidth; } }
        public int TextureHeight { get { return _textureHeight; } }

        /// <summary>Subscribe to transport events and prepare the texture. Safe to call repeatedly.</summary>
        public void Attach()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException("StreamReceiverCore");
            }

            if (_attached)
            {
                return;
            }

            EnsureTexture();
            _transport.FrameReceived += OnFrameReceived;
            _transport.ControlReceived += OnControlReceived;
            _attached = true;
            _nextDecodeTime = 0f;
        }

        /// <summary>Unsubscribe from events and destroy the texture.</summary>
        public void Detach()
        {
            if (_attached)
            {
                _transport.FrameReceived -= OnFrameReceived;
                _transport.ControlReceived -= OnControlReceived;
                _attached = false;
            }

            ReleaseTexture();
        }

        /// <summary>Call every frame from the main thread. Drives transport.Poll().</summary>
        public void Poll()
        {
            if (_disposed)
            {
                return;
            }

            _transport.Poll();
            UpdateStatistics();
        }

        public void ResetStatistics()
        {
            _decodedFrames = 0;
            _skippedFrames = 0;
            _decodeErrors = 0;
            _decodedInWindow = 0;
            _statAccumulator = 0f;
            _receiveFps = 0f;
            _lastDecodeMilliseconds = 0;
            _lastBytes = 0;
            _textureWidth = 0;
            _textureHeight = 0;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Detach();
            _decodePool.Clear();
        }

        private void EnsureTexture()
        {
            if (_texture != null)
            {
                return;
            }

            // Start with a small texture; LoadImage resizes it to the JPEG dimensions.
            // This single texture is reused every frame instead of allocating a new one.
            _texture = new Texture2D(2, 2, TextureFormat.RGBA32, false, false)
            {
                name = "FXM.StreamTexture",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
        }

        private void ReleaseTexture()
        {
            if (_texture == null)
            {
                return;
            }

            if (Application.isPlaying)
            {
                UnityEngine.Object.Destroy(_texture);
            }
            else
            {
                UnityEngine.Object.DestroyImmediate(_texture);
            }

            _texture = null;
        }

        private void OnControlReceived(ArraySegment<byte> payload)
        {
            StreamControlMessage message;
            if (!StreamControlProtocol.TryRead(payload.Array, payload.Offset, payload.Count, out message))
            {
                return;
            }

            var handler = ControlReceived;
            if (handler != null)
            {
                handler(message);
            }
        }

        private void OnFrameReceived(ArraySegment<byte> frame)
        {
            if (frame.Array == null || frame.Count <= 0)
            {
                return;
            }

            // Audio shares the frame path with JPEG. It must be handled before the video
            // throttle so pause/seek do not silence an otherwise playing stream, and so
            // the JPEG decoder never sees it (even when the audio header is malformed).
            if (StreamAudioProtocol.IsAudioPacket(frame.Array, frame.Offset, frame.Count))
            {
                StreamAudioFrame audio;
                if (StreamAudioProtocol.TryRead(frame.Array, frame.Offset, frame.Count, out audio))
                {
                    var audioHandler = AudioReceived;
                    if (audioHandler != null)
                    {
                        audioHandler(audio);
                    }
                }

                return;
            }

            if (!AcceptFrames || _texture == null)
            {
                return;
            }

            // Throttle decoding: skip frames once the per-second decode budget is reached.
            if (Time.unscaledTime < _nextDecodeTime)
            {
                _skippedFrames++;
                return;
            }

            _nextDecodeTime = Time.unscaledTime + 1f / Mathf.Max(1, _maxDecodeFps);

            // LoadImage needs a complete array. The transport buffer can be larger than the
            // frame (pooled), so copy only when needed; the copy buffer is pooled too.
            byte[] jpeg = frame.Array;
            bool copied = frame.Offset != 0 || frame.Count != jpeg.Length;
            if (copied)
            {
                jpeg = _decodePool.Rent(frame.Count);
                Buffer.BlockCopy(frame.Array, frame.Offset, jpeg, 0, frame.Count);
            }

            var stopwatch = Stopwatch.StartNew();
            bool decoded;

            try
            {
                decoded = _texture.LoadImage(jpeg, _markTextureNonReadable);
            }
            catch (Exception exception)
            {
                decoded = false;
                UnityEngine.Debug.LogWarning("[FXM.WebStream] LoadImage exception: " + exception.Message);
            }
            finally
            {
                if (copied)
                {
                    _decodePool.Return(jpeg);
                }
            }

            stopwatch.Stop();

            if (!decoded)
            {
                _decodeErrors++;
                if (!_loggedDecodeError)
                {
                    _loggedDecodeError = true;
                    UnityEngine.Debug.LogWarning("[FXM.WebStream] JPEG decode failed. Check whether the frame is truncated or not a JPEG.");
                }

                return;
            }

            _decodedFrames++;
            _decodedInWindow++;
            _lastBytes = frame.Count;
            _lastDecodeMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
            _textureWidth = _texture.width;
            _textureHeight = _texture.height;

            var handler = FrameDecoded;
            if (handler != null)
            {
                handler(_texture);
            }
        }

        private void UpdateStatistics()
        {
            _statAccumulator += Time.unscaledDeltaTime;
            if (_statAccumulator < 1f)
            {
                return;
            }

            _receiveFps = _decodedInWindow / _statAccumulator;
            _decodedInWindow = 0;
            _statAccumulator = 0f;
        }
    }
}
