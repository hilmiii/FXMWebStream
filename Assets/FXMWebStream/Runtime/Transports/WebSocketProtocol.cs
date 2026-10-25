using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace FXM.WebStream
{
    /// <summary>
    /// WebSocket handshake and framing helpers (RFC 6455) for the TcpListener server.
    /// Written manually so the server runs on standalone players without HttpListener
    /// (which needs a Windows urlacl/admin for non-localhost prefixes) and without external
    /// package dependencies.
    /// </summary>
    public static class WebSocketProtocol
    {
        public const string HandshakeGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
        public const int OpcodeContinuation = 0x0;
        public const int OpcodeText = 0x1;
        public const int OpcodeBinary = 0x2;
        public const int OpcodeClose = 0x8;
        public const int OpcodePing = 0x9;
        public const int OpcodePong = 0xA;

        public const int MaxHandshakeBytes = 16 * 1024;
        public const int MaxMessageBytes = UdpFrameProtocol.MaxFrameBytes;

        /// <summary>Parse an HTTP upgrade request. The key must be present for a valid handshake.</summary>
        public static bool TryParseHandshake(byte[] request, int length, out string path, out string key)
        {
            path = null;
            key = null;

            if (request == null || length <= 0)
            {
                return false;
            }

            string text = Encoding.UTF8.GetString(request, 0, length);
            string[] lines = text.Split(new[] { "\r\n" }, StringSplitOptions.None);
            if (lines.Length < 2)
            {
                return false;
            }

            string[] requestLine = lines[0].Split(' ');
            if (requestLine.Length < 2 || !string.Equals(requestLine[0], "GET", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            path = requestLine[1];
            int queryIndex = path.IndexOf('?');
            if (queryIndex >= 0)
            {
                path = path.Substring(0, queryIndex);
            }

            for (int i = 1; i < lines.Length; i++)
            {
                int separator = lines[i].IndexOf(':');
                if (separator <= 0)
                {
                    continue;
                }

                string name = lines[i].Substring(0, separator).Trim();
                if (string.Equals(name, "Sec-WebSocket-Key", StringComparison.OrdinalIgnoreCase))
                {
                    key = lines[i].Substring(separator + 1).Trim();
                }
            }

            return !string.IsNullOrEmpty(key);
        }

        public static string CreateAcceptKey(string clientKey)
        {
            using (var sha1 = SHA1.Create())
            {
                byte[] hash = sha1.ComputeHash(Encoding.ASCII.GetBytes(clientKey + HandshakeGuid));
                return Convert.ToBase64String(hash);
            }
        }

        public static string BuildHandshakeResponse(string acceptKey)
        {
            return "HTTP/1.1 101 Switching Protocols\r\n" +
                   "Upgrade: websocket\r\n" +
                   "Connection: Upgrade\r\n" +
                   "Sec-WebSocket-Accept: " + acceptKey + "\r\n" +
                   "\r\n";
        }

        public static string BuildHttpErrorResponse(int statusCode, string reason)
        {
            string body = reason + "\n";
            return "HTTP/1.1 " + statusCode + " " + reason + "\r\n" +
                   "Content-Type: text/plain\r\n" +
                   "Content-Length: " + Encoding.UTF8.GetByteCount(body) + "\r\n" +
                   "Connection: close\r\n" +
                   "\r\n" + body;
        }

        /// <summary>Write a server frame header (unmasked, FIN=1). Buffer must be at least 10 bytes.</summary>
        public static int WriteServerFrameHeader(byte[] destination, int opcode, int payloadLength)
        {
            int offset = 0;
            destination[offset++] = (byte)(0x80 | (opcode & 0x0F));

            if (payloadLength < 126)
            {
                destination[offset++] = (byte)payloadLength;
            }
            else if (payloadLength <= 0xFFFF)
            {
                destination[offset++] = 126;
                destination[offset++] = (byte)(payloadLength >> 8);
                destination[offset++] = (byte)payloadLength;
            }
            else
            {
                destination[offset++] = 127;
                ulong length = (ulong)payloadLength;
                for (int shift = 56; shift >= 0; shift -= 8)
                {
                    destination[offset++] = (byte)(length >> shift);
                }
            }

            return offset;
        }

        /// <summary>Build a complete server frame (header + payload) in one buffer.</summary>
        public static byte[] BuildServerFrame(int opcode, byte[] payload, int offset, int length)
        {
            var frame = new byte[10 + length];
            int headerSize = WriteServerFrameHeader(frame, opcode, length);
            System.Buffer.BlockCopy(payload, offset, frame, headerSize, length);
            return frame;
        }

        /// <summary>Find the end of HTTP headers (\r\n\r\n). Returns -1 when incomplete.</summary>
        public static int FindHeaderEnd(byte[] buffer, int length)
        {
            for (int i = 3; i < length; i++)
            {
                if (buffer[i - 3] == '\r' && buffer[i - 2] == '\n' && buffer[i - 1] == '\r' && buffer[i] == '\n')
                {
                    return i + 1;
                }
            }

            return -1;
        }

        /// <summary>Read requested client subprotocols (optional, for logging).</summary>
        public static List<string> ParseSubProtocols(string headerValue)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(headerValue))
            {
                return result;
            }

            string[] parts = headerValue.Split(',');
            for (int i = 0; i < parts.Length; i++)
            {
                result.Add(parts[i].Trim());
            }

            return result;
        }
    }
}
