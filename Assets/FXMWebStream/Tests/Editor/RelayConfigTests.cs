using FXM.WebStream;
using NUnit.Framework;

namespace FXM.WebStream.Tests
{
    public sealed class RelayConfigTests
    {
        [Test]
        public void RelayConfigBuildsRoomRoleAndTokenQuery()
        {
            var config = TransportConfig.CreateRelay(
                "wss://relay.example.com/fxm-relay", "my room", "secret token", RelayPeerRole.Sender);

            string url = config.BuildWebSocketUrl();

            StringAssert.StartsWith("wss://relay.example.com", url);
            StringAssert.Contains("/fxm-relay?", url);
            StringAssert.Contains("room=my%20room", url);
            StringAssert.Contains("role=sender", url);
            StringAssert.Contains("token=secret%20token", url);
            Assert.IsTrue(config.AutoReconnect);
            Assert.AreEqual(TransportRole.Bidirectional, config.Role);
        }

        [Test]
        public void RelayTokenWhitespaceIsTrimmed()
        {
            var config = TransportConfig.CreateRelay(
                "ws://127.0.0.1:8787/fxm-relay", "demo", "  demo-token \t", RelayPeerRole.Receiver);

            string url = config.BuildWebSocketUrl();

            StringAssert.Contains("token=demo-token", url);
            StringAssert.DoesNotContain("token=%20", url);
        }

        [Test]
        public void RelayUrlWithoutSchemeIsAccepted()
        {
            var config = TransportConfig.CreateRelay("relay.example.com:8787", "demo", "", RelayPeerRole.Sender);

            Assert.AreEqual("relay.example.com", config.RemoteHost);
            Assert.AreEqual(8787, config.RemotePort);
            Assert.AreEqual("/fxm-relay", config.WebSocketPath);
            StringAssert.StartsWith("ws://relay.example.com:8787/fxm-relay?", config.BuildWebSocketUrl());
        }

        [Test]
        public void RelayUrlWithoutPathUsesRelayPath()
        {
            var config = TransportConfig.CreateRelay("ws://127.0.0.1:8787", "demo", "", RelayPeerRole.Receiver);

            Assert.AreEqual("/fxm-relay", config.WebSocketPath);
            StringAssert.Contains(":8787/fxm-relay?", config.BuildWebSocketUrl());
        }

        [Test]
        public void HttpsUrlMapsToSecureWebSocket()
        {
            var config = TransportConfig.ParseWebSocketUrl("https://relay.example.com/fxm-relay");

            Assert.IsTrue(config.WebSocketSecure);
            Assert.AreEqual(443, config.RemotePort);
            StringAssert.StartsWith("wss://relay.example.com", config.BuildWebSocketUrl());
        }

        [Test]
        public void BareWebSocketUrlKeepsStreamPath()
        {
            var config = TransportConfig.WebSocketClient("127.0.0.1:47778");

            Assert.AreEqual("127.0.0.1", config.RemoteHost);
            Assert.AreEqual(47778, config.RemotePort);
            Assert.AreEqual("/fxm-stream/", config.WebSocketPath);
        }

        [Test]
        public void ReceiverRoleIsEncoded()
        {
            var config = TransportConfig.CreateRelay("ws://localhost:8787/fxm-relay", "demo", "", RelayPeerRole.Receiver);

            string url = config.BuildWebSocketUrl();

            StringAssert.Contains("role=receiver", url);
            StringAssert.DoesNotContain("token=", url);
        }

        [Test]
        public void PlayerRoleIsEncoded()
        {
            var config = TransportConfig.CreateRelay("ws://localhost:8787/fxm-relay", "world", "", RelayPeerRole.Player);

            string url = config.BuildWebSocketUrl();

            StringAssert.Contains("role=player", url);
            Assert.IsTrue(config.AutoReconnect);
        }

        [Test]
        public void RelayKindCreatesWebSocketTransport()
        {
            Assert.IsInstanceOf<WebSocketTransport>(TransportFactory.Create(TransportKind.Relay));
        }

        [Test]
        public void LocalVideoPathsBecomeFileUrls()
        {
            Assert.AreEqual("file:///D:/media/clip.mp4", VideoPlayerStreamSource.NormalizeUrl("D:\\media\\clip.mp4"));
            Assert.AreEqual("file:///home/user/clip.mp4", VideoPlayerStreamSource.NormalizeUrl("/home/user/clip.mp4"));
            Assert.AreEqual("https://example.com/clip.mp4", VideoPlayerStreamSource.NormalizeUrl("https://example.com/clip.mp4"));
            Assert.AreEqual("file:///D:/media/clip.mp4", VideoPlayerStreamSource.NormalizeUrl("file:///D:/media/clip.mp4"));
        }
    }
}
