using System.Collections;
using FXM.WebStream.WalkingSim;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace FXM.WebStream.Tests
{
    public sealed class WalkingSimPlayModeTests
    {
        private const float PlayerWaitSeconds = 30f;

        [UnitySetUp]
        public IEnumerator LoadDemoScene()
        {
            CleanupWalkingSimScene();

            Scene existing = SceneManager.GetSceneByName("FXMWebStream");
            if (existing.isLoaded)
            {
                AsyncOperation unload = SceneManager.UnloadSceneAsync(existing);
                if (unload != null)
                {
                    yield return unload;
                }
            }

            // The first load in a batch run can also import the FBX and the city, which
            // takes longer than a frame or two, so always wait for the scene itself.
            yield return SceneManager.LoadSceneAsync("FXMWebStream", LoadSceneMode.Additive);
        }

        [UnityTearDown]
        public IEnumerator UnloadDemoScene()
        {
            CleanupWalkingSimScene();

            Scene scene = SceneManager.GetSceneByName("FXMWebStream");
            if (scene.isLoaded)
            {
                AsyncOperation unload = SceneManager.UnloadSceneAsync(scene);
                if (unload != null)
                {
                    yield return unload;
                }
            }
        }

        [UnityTest]
        public IEnumerator DemoSceneBuildsPlayableWalkingSim()
        {
            WalkingSimPlayer player = null;
            yield return WaitForPlayer(result => player = result);

            Assert.IsNotNull(player, "The walking sim did not create a local player.");

            var controller = player.GetComponent<CharacterController>();
            Assert.IsNotNull(controller, "The player has no CharacterController.");
            Assert.Greater(controller.height, 1f);

            var guyAnimator = player.GetComponent<GuyAnimator>();
            Assert.IsNotNull(guyAnimator, "The player has no GuyAnimator.");
            Assert.IsNotNull(guyAnimator.idleClip, "Idle clip is missing.");
            Assert.IsNotNull(guyAnimator.walkClip, "Walk clip is missing.");

            yield return null;
            yield return null;
            Assert.IsTrue(guyAnimator.IsReady, "The idle/walk Playable graph did not start.");

            Assert.IsNotNull(Object.FindAnyObjectByType<WalkingSimNetwork>(),
                "The relay player network component is missing.");
            Assert.IsNotNull(GameObject.Find("LowPolyCity (walking sim)"),
                "The city prefab was not instantiated.");
            Assert.IsNotNull(player.GetComponentInChildren<SkinnedMeshRenderer>(),
                "The Guy mesh did not instantiate.");

            var camera = player.followCamera;
            Assert.IsNotNull(camera, "No camera is available for streaming.");
            Assert.Less(Vector3.Distance(camera.transform.position, player.transform.position), 12f,
                "The third-person camera did not follow the player.");

            Assert.AreEqual(CameraClearFlags.Skybox, camera.clearFlags,
                "The gameplay camera should show the scene skybox.");

            var bootstrap = Object.FindAnyObjectByType<WalkingSimBootstrap>();
            Assert.IsNotNull(bootstrap, "The walking sim bootstrap is missing.");
            Assert.IsNotNull(bootstrap.gameplayCanvas, "The gameplay canvas is not assigned.");
            Assert.IsFalse(bootstrap.gameplayCanvas.activeSelf,
                "The FXM Canvas should be hidden in Stream gameplay mode.");

            var panel = Object.FindAnyObjectByType<StreamSetupPanel>();
            Assert.IsNotNull(panel, "The setup panel is missing.");
            panel.Mode = StreamSetupPanel.SetupMode.Watch;
            yield return null;
            Assert.IsTrue(bootstrap.gameplayCanvas.activeSelf,
                "The FXM Canvas should return when the panel switches to Watch mode.");
        }

        [UnityTest]
        public IEnumerator WasdMovesPlayerAndSwitchesToWalk()
        {
            WalkingSimPlayer player = null;
            yield return WaitForPlayer(result => player = result);

            Assert.IsNotNull(player, "The walking sim did not create a local player.");

            var panel = Object.FindAnyObjectByType<StreamSetupPanel>();
            if (panel != null)
            {
                panel.SetPanelHidden(true);
            }

            var guyAnimator = player.GetComponent<GuyAnimator>();
            Assert.IsNotNull(guyAnimator);

            Vector3 start = player.transform.position;
            Keyboard keyboard = InputSystem.AddDevice<Keyboard>();
            var previousEditorBehavior = InputSystem.settings.editorInputBehaviorInPlayMode;
            var previousBackground = InputSystem.settings.backgroundBehavior;

            try
            {
                // Batch-mode Game views do not take focus, which normally makes the editor
                // ignore keyboard input in Play mode. Manual state events need that off.
                InputSystem.settings.editorInputBehaviorInPlayMode =
                    InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
                InputSystem.settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;

                InputSystem.EnableDevice(keyboard);
                InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.W));
                InputSystem.Update();
                yield return null;

                Assert.IsTrue(Keyboard.current == keyboard && keyboard.wKey.isPressed,
                    "The simulated W key did not reach the Input System (current=" +
                    (Keyboard.current != null ? Keyboard.current.name : "none") +
                    ", added=" + keyboard.name +
                    ", enabled=" + keyboard.enabled +
                    ", mode=" + InputSystem.settings.updateMode +
                    ", value=" + keyboard.wKey.ReadValue() + ").");

                // Look at horizontal distance only: gravity also moves the character a
                // few centimetres while the ground is settling.
                float walkDeadline = Time.realtimeSinceStartup + 1.2f;
                while (Time.realtimeSinceStartup < walkDeadline)
                {
                    yield return null;
                }

                Vector3 travelled = player.transform.position - start;
                travelled.y = 0f;
                Assert.Greater(travelled.magnitude, 0.3f,
                    "Pressing W did not move the player (moving=" + player.IsMoving +
                    ", walkWeight=" + guyAnimator.WalkWeight +
                    ", key=" + Keyboard.current.wKey.isPressed +
                    ", grounded=" + player.GetComponent<CharacterController>().isGrounded + ").");
                Assert.IsTrue(player.IsMoving, "The player is moving but IsMoving is false.");
                Assert.Greater(guyAnimator.WalkWeight, 0.5f, "The walk clip did not fade in.");

                InputSystem.QueueStateEvent(keyboard, new KeyboardState());
                float idleDeadline = Time.realtimeSinceStartup + 0.8f;
                while (Time.realtimeSinceStartup < idleDeadline)
                {
                    yield return null;
                }

                Assert.Less(guyAnimator.WalkWeight, 0.5f, "The idle clip did not fade back in.");
            }
            finally
            {
                if (InputSystem.settings != null)
                {
                    InputSystem.settings.editorInputBehaviorInPlayMode = previousEditorBehavior;
                    InputSystem.settings.backgroundBehavior = previousBackground;
                }

                if (keyboard != null)
                {
                    InputSystem.RemoveDevice(keyboard);
                }
            }
        }

        [UnityTest]
        public IEnumerator MouseLookOrbitsTheCamera()
        {
            WalkingSimPlayer player = null;
            yield return WaitForPlayer(result => player = result);

            Assert.IsNotNull(player, "The walking sim did not create a local player.");

            var panel = Object.FindAnyObjectByType<StreamSetupPanel>();
            if (panel != null)
            {
                panel.SetPanelHidden(true);
            }

            yield return null;

            var camera = player.followCamera;
            Assert.IsNotNull(camera);
            Vector3 before = camera.transform.forward;

            Mouse mouse = InputSystem.AddDevice<Mouse>();
            var previousEditorBehavior = InputSystem.settings.editorInputBehaviorInPlayMode;
            var previousBackground = InputSystem.settings.backgroundBehavior;

            try
            {
                InputSystem.settings.editorInputBehaviorInPlayMode =
                    InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
                InputSystem.settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;

                InputSystem.EnableDevice(mouse);
                InputSystem.QueueStateEvent(mouse, new MouseState { delta = new Vector2(240f, 0f) });
                InputSystem.Update();
                yield return null;

                Assert.IsTrue(Mouse.current == mouse, "The simulated mouse is not current.");

                // Let the smoothed third-person camera settle on the new yaw. Batch-mode
                // frame pacing varies, so poll instead of assuming a fixed number of frames.
                float deadline = Time.realtimeSinceStartup + 2f;
                float angle = 0f;
                while (Time.realtimeSinceStartup < deadline)
                {
                    angle = Vector3.Angle(before, camera.transform.forward);
                    if (angle > 10f)
                    {
                        break;
                    }

                    yield return null;
                }

                Assert.Greater(angle, 10f, "Moving the mouse did not orbit the camera.");
            }
            finally
            {
                if (InputSystem.settings != null)
                {
                    InputSystem.settings.editorInputBehaviorInPlayMode = previousEditorBehavior;
                    InputSystem.settings.backgroundBehavior = previousBackground;
                }

                if (mouse != null)
                {
                    InputSystem.RemoveDevice(mouse);
                }
            }
        }

        [UnityTest]
        public IEnumerator RemoteAvatarBlocksTheLocalPlayer()
        {
            WalkingSimPlayer player = null;
            yield return WaitForPlayer(result => player = result);

            Assert.IsNotNull(player, "The walking sim did not create a local player.");

            var panel = Object.FindAnyObjectByType<StreamSetupPanel>();
            if (panel != null)
            {
                panel.SetPanelHidden(true);
            }

            yield return null;

            var remoteObject = new GameObject("Remote test avatar");
            try
            {
                var capsule = remoteObject.AddComponent<CapsuleCollider>();
                capsule.center = new Vector3(0f, 0.95f, 0f);
                capsule.height = 1.9f;
                capsule.radius = 0.32f;

                var remote = remoteObject.AddComponent<RemotePlayerAvatar>();
                remote.Configure(0xCAFEu, null, new WalkingSimState
                {
                    PlayerId = 0xCAFEu,
                    Position = player.transform.position,
                    Yaw = 0f,
                    Moving = false
                });

                float contactDeadline = Time.realtimeSinceStartup + 1.5f;
                while (Time.realtimeSinceStartup < contactDeadline)
                {
                    yield return null;
                }

                Vector3 separation = player.transform.position - remoteObject.transform.position;
                separation.y = 0f;
                Assert.Greater(separation.magnitude, 0.45f,
                    "The local player stayed inside another player's collider.");
            }
            finally
            {
                Object.Destroy(remoteObject);
            }
        }

        private static IEnumerator WaitForPlayer(System.Action<WalkingSimPlayer> assign)
        {
            float deadline = Time.realtimeSinceStartup + PlayerWaitSeconds;
            WalkingSimPlayer player = null;

            while (Time.realtimeSinceStartup < deadline && player == null)
            {
                player = Object.FindAnyObjectByType<WalkingSimPlayer>();
                yield return null;
            }

            assign(player);
        }

        private static void CleanupWalkingSimScene()
        {
            // The demo scene adds its runtime objects to the active scene, so remove
            // them explicitly before unloading the additive scene.
            var localPlayer = Object.FindAnyObjectByType<WalkingSimPlayer>();
            if (localPlayer != null)
            {
                Object.Destroy(localPlayer.gameObject);
            }

            GameObject city = GameObject.Find("LowPolyCity (walking sim)");
            if (city != null)
            {
                Object.Destroy(city);
            }

            GameObject ground = GameObject.Find("Walking Sim Ground");
            if (ground != null)
            {
                Object.Destroy(ground);
            }
        }
    }
}
