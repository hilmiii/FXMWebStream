using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using FXM.WebStream;
using FXM.WebStream.WalkingSim;
using NUnit.Framework;
using Debug = UnityEngine.Debug;

namespace FXM.WebStream.Tests
{
    public sealed class RelayTransportTests
    {
        [Test]
        public void RelayTransportForwardsFramesAndControl()
        {
            string projectRoot = Directory.GetParent(UnityEngine.Application.dataPath).FullName;
            string relayDirectory = Path.Combine(projectRoot, "Tools", "RelayServer");
            string serverScript = Path.Combine(relayDirectory, "server.js");

            if (!File.Exists(serverScript))
            {
                Assert.Ignore("Relay server not found at " + serverScript);
            }

            int port = GetFreePort();
            string room = "unity-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            const string token = "test-token";
            Process relay = StartRelay(relayDirectory, port, token);

            var sender = new WebSocketTransport();
            var receiver = new WebSocketTransport();
            byte[] receivedFrame = null;
            byte[] receivedControl = null;

            try
            {
                WaitForPort(port, 5000);

                sender.Log += message => Debug.Log("[relay-test][sender] " + message);
                receiver.Log += message => Debug.Log("[relay-test][receiver] " + message);
                receiver.FrameReceived += frame =>
                {
                    receivedFrame = new byte[frame.Count];
                    Buffer.BlockCopy(frame.Array, frame.Offset, receivedFrame, 0, frame.Count);
                };
                sender.ControlReceived += control =>
                {
                    receivedControl = new byte[control.Count];
                    Buffer.BlockCopy(control.Array, control.Offset, receivedControl, 0, control.Count);
                };

                Assert.IsTrue(sender.Start(TransportConfig.CreateRelay(
                    "ws://127.0.0.1:" + port + "/fxm-relay", room, token, RelayPeerRole.Sender)));
                Assert.IsTrue(receiver.Start(TransportConfig.CreateRelay(
                    "ws://127.0.0.1:" + port + "/fxm-relay", room, token, RelayPeerRole.Receiver)));

                WaitUntil(() => sender.PeerCount == 1 && receiver.PeerCount == 1, 8000,
                    "Both relay clients must connect.");

                var frame = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4, 5 };
                sender.Send(new ArraySegment<byte>(frame));

                WaitUntil(() =>
                {
                    receiver.Poll();
                    return receivedFrame != null;
                }, 5000, "Receiver did not get the frame through the relay.");

                CollectionAssert.AreEqual(frame, receivedFrame);

                var control = StreamControlProtocol.Create(
                    StreamControlMessage.CommandMessage(StreamCommandType.Pause, 0));
                receiver.SendControl(new ArraySegment<byte>(control));

                WaitUntil(() =>
                {
                    sender.Poll();
                    return receivedControl != null;
                }, 5000, "Sender did not get the control message through the relay.");

                CollectionAssert.AreEqual(control, receivedControl);
            }
            finally
            {
                sender.Dispose();
                receiver.Dispose();

                if (relay != null && !relay.HasExited)
                {
                    relay.Kill();
                    relay.WaitForExit(3000);
                }

                relay?.Dispose();
            }
        }

        [Test]
        public void PlayerRelayForwardsGameplayStateToPeers()
        {
            string projectRoot = Directory.GetParent(UnityEngine.Application.dataPath).FullName;
            string relayDirectory = Path.Combine(projectRoot, "Tools", "RelayServer");
            string serverScript = Path.Combine(relayDirectory, "server.js");

            if (!File.Exists(serverScript))
            {
                Assert.Ignore("Relay server not found at " + serverScript);
            }

            int port = GetFreePort();
            string room = "walk-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            Process relay = StartRelay(relayDirectory, port, string.Empty);

            var playerA = new WebSocketTransport();
            var playerB = new WebSocketTransport();
            byte[] received = null;
            bool echoed = false;

            try
            {
                WaitForPort(port, 5000);

                playerA.FrameReceived += frame => echoed = true;
                playerB.FrameReceived += frame =>
                {
                    received = new byte[frame.Count];
                    Buffer.BlockCopy(frame.Array, frame.Offset, received, 0, frame.Count);
                };

                Assert.IsTrue(playerA.Start(TransportConfig.CreateRelay(
                    "ws://127.0.0.1:" + port + "/fxm-relay", room, string.Empty, RelayPeerRole.Player)));
                Assert.IsTrue(playerB.Start(TransportConfig.CreateRelay(
                    "ws://127.0.0.1:" + port + "/fxm-relay", room, string.Empty, RelayPeerRole.Player)));

                WaitUntil(() => playerA.PeerCount == 1 && playerB.PeerCount == 1, 8000,
                    "Both player clients must connect to the relay.");

                var state = new WalkingSimState
                {
                    PlayerId = 1234,
                    Sequence = 7,
                    Position = new UnityEngine.Vector3(1f, 2f, 3f),
                    Yaw = 90f,
                    Moving = true
                };

                var buffer = new byte[WalkingSimProtocol.PacketSize];
                int length = WalkingSimProtocol.Write(buffer, 0, state);
                playerA.Send(new ArraySegment<byte>(buffer, 0, length));

                WaitUntil(() =>
                {
                    playerB.Poll();
                    return received != null;
                }, 5000, "Player B did not receive gameplay state through the relay.");

                CollectionAssert.AreEqual(buffer, received);

                WalkingSimState decoded;
                Assert.IsTrue(WalkingSimProtocol.TryRead(received, 0, received.Length, out decoded));
                Assert.AreEqual(1234u, decoded.PlayerId);
                Assert.AreEqual(7u, decoded.Sequence);
                Assert.IsTrue(decoded.Moving);

                Thread.Sleep(250);
                playerA.Poll();
                Assert.IsFalse(echoed, "The relay echoed the player's own state back.");
            }
            finally
            {
                playerA.Dispose();
                playerB.Dispose();

                if (relay != null && !relay.HasExited)
                {
                    relay.Kill();
                    relay.WaitForExit(3000);
                }

                relay?.Dispose();
            }
        }

        private static Process StartRelay(string workingDirectory, int port, string token)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "node",
                Arguments = "server.js",
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            startInfo.EnvironmentVariables["PORT"] = port.ToString();
            startInfo.EnvironmentVariables["HOST"] = "127.0.0.1";
            startInfo.EnvironmentVariables["FXM_RELAY_TOKEN"] = token;

            try
            {
                return Process.Start(startInfo);
            }
            catch (Exception exception)
            {
                Assert.Ignore("Node.js is not available: " + exception.Message);
                return null;
            }
        }

        private static int GetFreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        private static void WaitForPort(int port, int timeoutMs)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    using (var client = new TcpClient())
                    {
                        client.Connect(IPAddress.Loopback, port);
                        return;
                    }
                }
                catch (SocketException)
                {
                    Thread.Sleep(100);
                }
            }

            Assert.Fail("Relay did not listen on port " + port + " within " + timeoutMs + " ms.");
        }

        private static void WaitUntil(Func<bool> condition, int timeoutMs, string message)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                if (condition())
                {
                    return;
                }

                Thread.Sleep(20);
            }

            Assert.Fail(message);
        }
    }
}
