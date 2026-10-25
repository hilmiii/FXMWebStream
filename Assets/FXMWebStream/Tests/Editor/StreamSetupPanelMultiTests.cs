using NUnit.Framework;
using UnityEngine;

namespace FXM.WebStream.Tests
{
    public sealed class StreamSetupPanelMultiTests
    {
        private GameObject _panelObject;
        private StreamSetupPanel _panel;

        [SetUp]
        public void SetUp()
        {
            _panelObject = new GameObject("FXM Multi Panel Test");
            _panel = _panelObject.AddComponent<StreamSetupPanel>();
            _panel.Mode = StreamSetupPanel.SetupMode.Multi;
            _panel.TransportOption = 0;
            _panel.RelayUrl = "ws://127.0.0.1:8787/fxm-relay";
            _panel.multiRooms = "demo2, demo3";
            _panel.multiUrls = string.Empty;
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(_panelObject);
        }

        [Test]
        public void RelayRoomsCanStartWithoutASingleReceiver()
        {
            string reason;
            Assert.IsTrue(_panel.CanStart(out reason), reason);
        }

        [Test]
        public void MultiSourceListParsesCommasAndLines()
        {
            _panel.multiRooms = "alpha\nbeta, gamma;alpha";

            CollectionAssert.AreEqual(new[] { "alpha", "beta", "gamma" }, _panel.MultiSourceList);
        }

        [Test]
        public void InvalidRoomIsRejected()
        {
            _panel.multiRooms = "demo 2";

            string reason;
            Assert.IsFalse(_panel.CanStart(out reason));
            StringAssert.Contains("Room", reason);
        }

        [Test]
        public void EmptySourceListIsRejected()
        {
            _panel.multiRooms = "";

            string reason;
            Assert.IsFalse(_panel.CanStart(out reason));
            StringAssert.Contains("room", reason.ToLowerInvariant());
        }

        [Test]
        public void WebSocketUrlsCanStart()
        {
            _panel.TransportOption = 1;
            _panel.multiUrls = "ws://192.168.1.20:47778/fxm-stream/\nws://192.168.1.21:47778/fxm-stream/";

            string reason;
            Assert.IsTrue(_panel.CanStart(out reason), reason);
        }

        [Test]
        public void RelayUrlIsRejectedOnWebSocketTab()
        {
            _panel.TransportOption = 1;
            _panel.multiUrls = "ws://192.168.1.20:8787/fxm-relay";

            string reason;
            Assert.IsFalse(_panel.CanStart(out reason));
            StringAssert.Contains("relay", reason.ToLowerInvariant());
        }

        [Test]
        public void UdpIsRejectedForManySources()
        {
            _panel.TransportOption = 2;

            string reason;
            Assert.IsFalse(_panel.CanStart(out reason));
            StringAssert.Contains("Relay", reason);
        }

        [Test]
        public void FullscreenGridUsesOneColumnInPortrait()
        {
            Assert.AreEqual(1, StreamSetupPanel.GetGridColumnCount(2, 0.45f, 16f / 9f));
            Assert.AreEqual(1, StreamSetupPanel.GetGridColumnCount(4, 0.45f, 16f / 9f));
        }

        [Test]
        public void FullscreenGridUsesTwoColumnsInLandscape()
        {
            Assert.AreEqual(2, StreamSetupPanel.GetGridColumnCount(2, 16f / 9f, 16f / 9f));
            Assert.AreEqual(2, StreamSetupPanel.GetGridColumnCount(3, 16f / 9f, 16f / 9f));
            Assert.AreEqual(2, StreamSetupPanel.GetGridColumnCount(4, 16f / 9f, 16f / 9f));
        }

        [Test]
        public void FullscreenGridPairsPortraitSourcesOnAPhone()
        {
            Assert.AreEqual(2, StreamSetupPanel.GetGridColumnCount(3, 0.45f, 9f / 16f));
            Assert.AreEqual(2, StreamSetupPanel.GetGridColumnCount(4, 0.45f, 9f / 16f));
            Assert.AreEqual(1, StreamSetupPanel.GetGridColumnCount(1, 0.45f, 16f / 9f));
        }
    }
}
