using System.Collections;
using FXM.WebStream;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UI;

namespace FXM.WebStream.Tests
{
    /// <summary>
    /// End-to-end tests:
    /// - camera -> RenderTexture -> AsyncGPUReadback -> encoder thread -> UDP -> reassembly ->
    ///   LoadImage -> RawImage, including round-trip play/pause/stop commands;
    /// - two UDP senders -> one MultiStreamReceiver (many-to-one).
    /// </summary>
    public sealed class LoopbackPlayModeTests
    {
        private const int Port = 47913;
        private const int ControlPort = Port + 1;

        private const int MultiPort = 47940;
        private const int MultiControlPort = 47960;

        private GameObject _root;

        [TearDown]
        public void TearDown()
        {
            if (_root != null)
            {
                Object.Destroy(_root);
            }
        }

        [UnityTest]
        public IEnumerator CameraStreamsToRawImageOverUdpLoopback()
        {
            _root = new GameObject("FXM Loopback Test");

            // Camera plus a visible object.
            var cameraObject = new GameObject("Test Camera");
            cameraObject.transform.SetParent(_root.transform, false);
            cameraObject.transform.position = new Vector3(0f, 0f, -6f);
            cameraObject.transform.LookAt(Vector3.zero);
            cameraObject.tag = "MainCamera";
            var camera = cameraObject.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.1f, 0.1f, 0.2f, 1f);

            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.transform.SetParent(_root.transform, false);
            cube.transform.position = new Vector3(0f, 0f, 0f);

            // Receiver UI.
            var canvasObject = new GameObject("Test Canvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasObject.transform.SetParent(_root.transform, false);
            var canvas = canvasObject.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;

            var rawObject = new GameObject("Test RawImage", typeof(RectTransform), typeof(RawImage));
            rawObject.transform.SetParent(canvasObject.transform, false);
            rawObject.GetComponent<RectTransform>().sizeDelta = new Vector2(320f, 180f);
            var rawImage = rawObject.GetComponent<RawImage>();

            // Receiver.
            var receiverObject = new GameObject("Test Receiver");
            receiverObject.transform.SetParent(_root.transform, false);
            var receiver = receiverObject.AddComponent<StreamingReceiver>();
            receiver.autoStart = false;
            receiver.transportKind = TransportKind.Udp;
            receiver.listenPort = Port;
            receiver.senderHost = "127.0.0.1";
            receiver.senderControlPort = ControlPort;
            receiver.targetRawImage = rawImage;
            receiver.maxDecodeFps = 30;
            receiver.markTextureNonReadable = false;

            // Sender.
            var senderObject = new GameObject("Test Sender");
            senderObject.transform.SetParent(_root.transform, false);
            var sender = senderObject.AddComponent<StreamingSender>();
            sender.autoStart = false;
            sender.sourceCamera = camera;
            sender.targetFps = 20;
            sender.jpegQuality = 65;
            sender.resolutionScale = 0.5f;
            sender.encoderThreads = 1;
            sender.captureMethod = CaptureMethod.AsyncGPUReadback;
            sender.transportKind = TransportKind.Udp;
            sender.remoteHost = "127.0.0.1";
            sender.remotePort = Port;
            sender.localControlPort = ControlPort;
            sender.allowRemoteControl = true;

            receiver.StartReceiving();
            sender.StartStreaming();

            try
            {
                Assert.IsTrue(sender.IsStreaming, "Sender failed to start (see Console).");
                Assert.IsTrue(receiver.IsReceiving, "Receiver failed to start (port already in use?).");

                float deadline = Time.realtimeSinceStartup + 8f;
                while (Time.realtimeSinceStartup < deadline && receiver.DecodedFrames < 3)
                {
                    yield return null;
                }

                Assert.Greater(receiver.DecodedFrames, 0, "No frame was decoded.");
                Assert.IsNotNull(rawImage.texture, "RawImage has not received a texture.");
                Assert.Greater(rawImage.texture.width, 16);
                Assert.Greater(rawImage.texture.height, 16);
                Assert.AreNotEqual(0u, receiver.SourceId, "Receiver has not received state from the sender.");

                // Pause: the receiver sends the command to the sender over the UDP control channel.
                receiver.Pause();
                deadline = Time.realtimeSinceStartup + 3f;
                while (Time.realtimeSinceStartup < deadline && sender.PlaybackState != StreamPlaybackState.Paused)
                {
                    yield return null;
                }

                Assert.AreEqual(StreamPlaybackState.Paused, sender.PlaybackState,
                    "Sender did not receive the Pause command.");
                Assert.AreEqual(StreamPlaybackState.Paused, receiver.PlaybackState);

                // Play again: the transport stays alive while paused.
                receiver.Play();
                deadline = Time.realtimeSinceStartup + 3f;
                while (Time.realtimeSinceStartup < deadline && sender.PlaybackState != StreamPlaybackState.Playing)
                {
                    yield return null;
                }

                Assert.AreEqual(StreamPlaybackState.Playing, sender.PlaybackState,
                    "Sender did not receive the Play command.");

                int framesBefore = receiver.DecodedFrames;
                yield return new WaitForSecondsRealtime(1f);
                Assert.Greater(receiver.DecodedFrames, framesBefore, "Frames did not resume after Play.");

                // Stop, then Play again.
                receiver.Stop();
                deadline = Time.realtimeSinceStartup + 3f;
                while (Time.realtimeSinceStartup < deadline && sender.PlaybackState != StreamPlaybackState.Stopped)
                {
                    yield return null;
                }

                Assert.AreEqual(StreamPlaybackState.Stopped, sender.PlaybackState,
                    "Sender did not receive the Stop command.");

                receiver.Play();
                deadline = Time.realtimeSinceStartup + 3f;
                while (Time.realtimeSinceStartup < deadline && sender.PlaybackState != StreamPlaybackState.Playing)
                {
                    yield return null;
                }

                int framesBeforeResume = receiver.DecodedFrames;
                yield return new WaitForSecondsRealtime(1f);
                Assert.Greater(receiver.DecodedFrames, framesBeforeResume,
                    "Frames did not resume after Stop -> Play.");

                Debug.Log(string.Format(
                    "[FXM Test] sent={0} packets={1} decoded={2} skipped={3} malformed={4} control={5} texture={6}x{7}",
                    sender.Transport.Statistics.FramesSent,
                    sender.Transport.Statistics.PacketsSent,
                    receiver.DecodedFrames,
                    receiver.SkippedFrames,
                    receiver.Transport.Statistics.PacketsMalformed,
                    sender.Transport.Statistics.ControlMessagesReceived,
                    rawImage.texture.width,
                    rawImage.texture.height));
            }
            finally
            {
                sender.StopStreaming();
                receiver.StopReceiving();
            }
        }

        [UnityTest]
        public IEnumerator TwoSendersFeedOneMultiReceiver()
        {
            _root = new GameObject("FXM Multi Test");

            var multiObject = new GameObject("Multi Receiver");
            multiObject.transform.SetParent(_root.transform, false);
            var multi = multiObject.AddComponent<MultiStreamReceiver>();
            multi.autoStart = false;
            multi.showAllSources = true;
            multi.maxDecodeFps = 30;
            multi.sources = new[]
            {
                new StreamSourceConfig
                {
                    name = "A",
                    transportKind = TransportKind.Udp,
                    host = "127.0.0.1",
                    localPort = MultiPort,
                    senderControlPort = MultiControlPort
                },
                new StreamSourceConfig
                {
                    name = "B",
                    transportKind = TransportKind.Udp,
                    host = "127.0.0.1",
                    localPort = MultiPort + 1,
                    senderControlPort = MultiControlPort + 1
                }
            };

            var senders = new StreamingSender[2];

            for (int i = 0; i < 2; i++)
            {
                var cameraObject = new GameObject("Source Camera " + i);
                cameraObject.transform.SetParent(_root.transform, false);
                cameraObject.transform.position = new Vector3(i * 4f, 1f, -6f);
                cameraObject.transform.LookAt(new Vector3(i * 4f, 1f, 0f));

                var camera = cameraObject.AddComponent<Camera>();
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(0.1f + i * 0.2f, 0.1f, 0.2f, 1f);

                var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                cube.transform.SetParent(_root.transform, false);
                cube.transform.position = new Vector3(i * 4f, 1f, 0f);

                var senderObject = new GameObject("Sender " + i);
                senderObject.transform.SetParent(_root.transform, false);
                var sender = senderObject.AddComponent<StreamingSender>();
                sender.autoStart = false;
                sender.sourceCamera = camera;
                sender.targetFps = 15;
                sender.jpegQuality = 60;
                sender.resolutionScale = 0.4f;
                sender.captureMethod = CaptureMethod.AsyncGPUReadback;
                sender.transportKind = TransportKind.Udp;
                sender.remoteHost = "127.0.0.1";
                sender.remotePort = MultiPort + i;
                sender.localControlPort = MultiControlPort + i;
                sender.allowRemoteControl = true;
                senders[i] = sender;
            }

            multi.StartReceiving();
            for (int i = 0; i < senders.Length; i++)
            {
                senders[i].StartStreaming();
            }

            try
            {
                Assert.IsTrue(multi.IsReceiving, "MultiStreamReceiver failed to start.");
                Assert.AreEqual(2, multi.SourceCount);

                float deadline = Time.realtimeSinceStartup + 10f;
                while (Time.realtimeSinceStartup < deadline)
                {
                    var textureA = multi.GetSourceTexture(0);
                    var textureB = multi.GetSourceTexture(1);

                    // The 2x2 texture is only the initial container; wait for a real frame.
                    if (textureA != null && textureA.width > 8 && textureB != null && textureB.width > 8)
                    {
                        break;
                    }

                    yield return null;
                }

                Assert.IsNotNull(multi.GetSourceTexture(0), "Source A received no frame.");
                Assert.IsNotNull(multi.GetSourceTexture(1), "Source B received no frame.");
                Assert.Greater(multi.GetSourceTexture(0).width, 8);
                Assert.Greater(multi.GetSourceTexture(1).width, 8);

                deadline = Time.realtimeSinceStartup + 3f;
                while (Time.realtimeSinceStartup < deadline &&
                       (multi.GetSourceId(0) == 0 || multi.GetSourceId(1) == 0))
                {
                    yield return null;
                }

                Assert.AreNotEqual(0u, multi.GetSourceId(0), "State from source A was not received.");
                Assert.AreNotEqual(0u, multi.GetSourceId(1), "State from source B was not received.");

                // Pause is sent to both sources (showAllSources = true).
                multi.Pause();
                deadline = Time.realtimeSinceStartup + 3f;
                while (Time.realtimeSinceStartup < deadline &&
                       (senders[0].PlaybackState != StreamPlaybackState.Paused ||
                        senders[1].PlaybackState != StreamPlaybackState.Paused))
                {
                    yield return null;
                }

                Assert.AreEqual(StreamPlaybackState.Paused, senders[0].PlaybackState, "Sender A did not pause.");
                Assert.AreEqual(StreamPlaybackState.Paused, senders[1].PlaybackState, "Sender B did not pause.");

                multi.Play();
                deadline = Time.realtimeSinceStartup + 3f;
                while (Time.realtimeSinceStartup < deadline &&
                       (senders[0].PlaybackState != StreamPlaybackState.Playing ||
                        senders[1].PlaybackState != StreamPlaybackState.Playing))
                {
                    yield return null;
                }

                Assert.AreEqual(StreamPlaybackState.Playing, senders[0].PlaybackState, "Sender A did not play.");
                Assert.AreEqual(StreamPlaybackState.Playing, senders[1].PlaybackState, "Sender B did not play.");

                Debug.Log(string.Format(
                    "[FXM Multi Test] sourceA={0}x{1} sourceB={2}x{3}",
                    multi.GetSourceTexture(0).width, multi.GetSourceTexture(0).height,
                    multi.GetSourceTexture(1).width, multi.GetSourceTexture(1).height));
            }
            finally
            {
                for (int i = 0; i < senders.Length; i++)
                {
                    senders[i].StopStreaming();
                }

                multi.StopReceiving();
            }
        }
    }
}
