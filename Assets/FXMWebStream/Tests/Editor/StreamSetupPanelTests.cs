using System.IO;
using FXM.WebStream;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Video;

namespace FXM.WebStream.Tests
{
    public sealed class StreamSetupPanelTests
    {
        private GameObject _panelObject;
        private GameObject _senderObject;
        private GameObject _receiverObject;
        private StreamSetupPanel _panel;

        [SetUp]
        public void SetUp()
        {
            _panelObject = new GameObject("FXM Setup Panel Test");
            _panel = _panelObject.AddComponent<StreamSetupPanel>();

            _senderObject = new GameObject("FXM Sender Test");
            _panel.sender = _senderObject.AddComponent<StreamingSender>();

            _receiverObject = new GameObject("FXM Receiver Test");
            _panel.receiver = _receiverObject.AddComponent<StreamingReceiver>();

            _panel.Mode = StreamSetupPanel.SetupMode.Stream;
            _panel.TransportOption = 0;
            _panel.RelayUrl = "ws://127.0.0.1:8787/fxm-relay";
            _panel.RelayRoom = "demo";
            _panel.VideoInput = StreamSetupPanel.VideoInputMode.Url;
            _panel.VideoUrl = "https://example.com/clip.mp4";
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(_panelObject);
            Object.DestroyImmediate(_senderObject);
            Object.DestroyImmediate(_receiverObject);
        }

        [Test]
        public void ValidRelaySettingsCanStart()
        {
            string reason;
            Assert.IsTrue(_panel.CanStart(out reason), reason);
        }

        [Test]
        public void RelayUrlWithoutSchemeIsAccepted()
        {
            _panel.RelayUrl = "127.0.0.1:8787";

            string reason;
            Assert.IsTrue(_panel.CanStart(out reason), reason);
        }

        [Test]
        public void InvalidRoomIsRejected()
        {
            _panel.RelayRoom = "not a room!";

            string reason;
            Assert.IsFalse(_panel.CanStart(out reason));
            StringAssert.Contains("Room", reason);
        }

        [Test]
        public void EmptyRelayUrlIsRejected()
        {
            _panel.RelayUrl = "";

            string reason;
            Assert.IsFalse(_panel.CanStart(out reason));
            StringAssert.Contains("Relay URL", reason);
        }

        [Test]
        public void EmptyVideoUrlIsRejected()
        {
            _panel.VideoInput = StreamSetupPanel.VideoInputMode.Url;
            _panel.VideoUrl = "";

            string reason;
            Assert.IsFalse(_panel.CanStart(out reason));
            StringAssert.Contains("video", reason.ToLowerInvariant());
        }

        [Test]
        public void LocalVideoFileCanStartClipMode()
        {
            string path = Path.Combine(Path.GetTempPath(), "fxm-local-" + System.Guid.NewGuid().ToString("N") + ".mp4");
            File.WriteAllBytes(path, new byte[] { 0 });

            try
            {
                _panel.clips = new VideoClip[0];
                _panel.VideoInput = StreamSetupPanel.VideoInputMode.Clip;
                _panel.localClipPath = path;

                string reason;
                Assert.IsTrue(_panel.CanStart(out reason), reason);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Test]
        public void MissingLocalVideoFileIsRejected()
        {
            _panel.clips = new VideoClip[0];
            _panel.VideoInput = StreamSetupPanel.VideoInputMode.Clip;
            _panel.localClipPath = Path.Combine(Path.GetTempPath(),
                "fxm-missing-" + System.Guid.NewGuid().ToString("N") + ".mp4");

            string reason;
            Assert.IsFalse(_panel.CanStart(out reason));
            StringAssert.Contains("no longer exists", reason);
        }

        [Test]
        public void SupportedVideoExtensionsAreCaseInsensitive()
        {
            Assert.IsTrue(StreamSetupPanel.IsSupportedVideoFile("clip.MP4"));
            Assert.IsTrue(StreamSetupPanel.IsSupportedVideoFile("/tmp/movie.webm"));
            Assert.IsTrue(StreamSetupPanel.IsSupportedVideoFile("D:\\media\\take.mov"));
            Assert.IsFalse(StreamSetupPanel.IsSupportedVideoFile("notes.txt"));
            Assert.IsFalse(StreamSetupPanel.IsSupportedVideoFile(string.Empty));
        }

        [Test]
        public void WatchRequiresReceiver()
        {
            _panel.Mode = StreamSetupPanel.SetupMode.Watch;
            _panel.receiver = null;

            string reason;
            Assert.IsFalse(_panel.CanStart(out reason));
            StringAssert.Contains("StreamingReceiver", reason);
        }

        [Test]
        public void WatchWithReceiverAndRelayCanStart()
        {
            _panel.Mode = StreamSetupPanel.SetupMode.Watch;

            string reason;
            Assert.IsTrue(_panel.CanStart(out reason), reason);
        }

        [Test]
        public void WatchOnWebSocketTabRejectsRelayUrl()
        {
            _panel.Mode = StreamSetupPanel.SetupMode.Watch;
            _panel.TransportOption = 1;
            _panel.WebSocketUrl = "ws://192.168.2.246:8787/fxm-relay";

            string reason;
            Assert.IsFalse(_panel.CanStart(out reason));
            StringAssert.Contains("Relay", reason);
        }
    }
}
