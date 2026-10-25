#if UNITY_WEBGL && !UNITY_EDITOR
using System;
using System.Runtime.InteropServices;

namespace FXM.WebStream
{
    /// <summary>
    /// Thin bridge to the JavaScript plugin (Assets/FXMWebStream/Plugins/WebGL/FXMWebSocket.jslib).
    /// The browser handles the handshake, TLS (wss), and WebSocket framing; C# only polls
    /// binary messages that have already arrived.
    /// </summary>
    internal sealed class WebGLWebSocketClient : IDisposable
    {
        [DllImport("__Internal")]
        private static extern int FXM_WS_Connect(string url);

        [DllImport("__Internal")]
        private static extern int FXM_WS_Poll(int id, byte[] destination, int capacity);

        [DllImport("__Internal")]
        private static extern int FXM_WS_Send(int id, byte[] data, int length);

        [DllImport("__Internal")]
        private static extern int FXM_WS_GetState(int id);

        [DllImport("__Internal")]
        private static extern void FXM_WS_Close(int id);

        private readonly FrameBufferPool _sendPool = new FrameBufferPool(2);
        private int _id = -1;
        private bool _closed;

        public bool Connect(string url)
        {
            if (_id > 0)
            {
                return true;
            }

            try
            {
                _id = FXM_WS_Connect(url);
            }
            catch (Exception)
            {
                _id = -1;
            }

            return _id > 0;
        }

        public bool IsOpen
        {
            get { return !_closed && _id > 0 && FXM_WS_GetState(_id) == 1; }
        }

        /// <summary>
        /// Read one message from the browser. Returns the message length, 0 when none is
        /// available, -1 when the message was too large (dropped), or -2 when closed.
        /// </summary>
        public int PollMessage(byte[] destination)
        {
            if (_closed || _id <= 0)
            {
                return -2;
            }

            int result = FXM_WS_Poll(_id, destination, destination.Length);
            if (result == -2)
            {
                _closed = true;
            }

            return result;
        }

        public bool Send(byte[] source, int offset, int length)
        {
            if (!IsOpen || source == null || length <= 0)
            {
                return false;
            }

            if (offset == 0)
            {
                return FXM_WS_Send(_id, source, length) != 0;
            }

            // DllImport cannot take an offset; copy only when the segment does not start at 0.
            byte[] copy = _sendPool.Rent(length);
            Buffer.BlockCopy(source, offset, copy, 0, length);
            bool sent = FXM_WS_Send(_id, copy, length) != 0;
            _sendPool.Return(copy);
            return sent;
        }

        public void Dispose()
        {
            if (_id > 0)
            {
                try
                {
                    FXM_WS_Close(_id);
                }
                catch (Exception)
                {
                    // Ignored.
                }
            }

            _id = -1;
            _closed = true;
        }
    }
}
#endif
