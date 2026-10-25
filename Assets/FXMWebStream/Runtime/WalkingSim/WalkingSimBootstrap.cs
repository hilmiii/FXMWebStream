using UnityEngine;
using UnityEngine.Rendering;

namespace FXM.WebStream.WalkingSim
{
    /// <summary>
    /// Builds the walking-sim world at runtime from the assets in Assets/Prefabs:
    /// the LowPolyCity prefab, a large ground collider, and the Guy model with a
    /// third-person camera. The existing stream scene camera is reused, so whatever the
    /// player sees is exactly what <see cref="StreamingSender"/> captures.
    /// </summary>
    [DefaultExecutionOrder(-90)]
    [AddComponentMenu("FXM WebStream/Walking Sim/Walking Sim Bootstrap")]
    public sealed class WalkingSimBootstrap : MonoBehaviour
    {
        [Header("Assets (Assets/Prefabs)")]
        [Tooltip("Guy.fbx, the animated character.")]
        public GameObject guyPrefab;

        [Tooltip("LowPolyCity.prefab, the city used as the walkable backdrop.")]
        public GameObject cityPrefab;

        [Tooltip("Rig|idle animation clip from Guy.fbx.")]
        public AnimationClip idleClip;

        [Tooltip("Rig|walk animation clip from Guy.fbx.")]
        public AnimationClip walkClip;

        [Header("Scene")]
        [Tooltip("Camera used for gameplay and streaming. Falls back to Camera.main.")]
        public Camera playerCamera;

        [Tooltip("Where the local player starts, in the city's world coordinates.")]
        public Vector3 spawnPoint = new Vector3(-61.17f, 16.9f, -76.66f);

        [Tooltip("Flat ground height. Slightly below the city road surfaces.")]
        public float groundHeight = 16.7f;

        [Tooltip("Add mesh colliders to the city so buildings and props are solid.")]
        public bool addCityColliders = true;

        [Header("Movement")]
        public float walkSpeed = 3.2f;

        public float cameraDistance = 4f;

        [Tooltip("Mouse look speed on desktop.")]
        public float lookSensitivity = 0.11f;

        [Tooltip("Hide the setup panel automatically when streaming starts, so the game is visible.")]
        public bool autoHideSetupPanel = true;

        [Tooltip("Optional uGUI canvas (FXM Canvas) hidden during gameplay so the 3D view and skybox are unobstructed.")]
        public GameObject gameplayCanvas;

        [Header("Multiplayer (relay player room)")]
        [Tooltip("Use the same relay URL and game room on every device.")]
        public string relayUrl = "ws://127.0.0.1:8787/fxm-relay";

        [Tooltip("Shared world room. This is independent from the video sender/receiver room.")]
        public string gameRoom = "demo";

        [Tooltip("Optional relay token. Leave empty when the relay has no token.")]
        public string relayToken = string.Empty;

        private GameObject _city;
        private Material _groundMaterial;
        private WalkingSimPlayer _player;
        private WalkingSimNetwork _network;
        private StreamSetupPanel _setupPanel;
        private bool _built;
        private bool _autoHidden;
        private bool _canvasVisibleInWatch;

        private void Start()
        {
            Build();
        }

        private void Update()
        {
            UpdateGameplayCanvas();

            if (!autoHideSetupPanel || _setupPanel == null)
            {
                return;
            }

            bool streaming = _setupPanel.IsStarted &&
                             _setupPanel.Mode == StreamSetupPanel.SetupMode.Stream;

            if (!streaming)
            {
                _autoHidden = false;
                return;
            }

            if (!_autoHidden && !_setupPanel.IsPanelHidden)
            {
                _setupPanel.SetPanelHidden(true);
                _autoHidden = true;
            }
        }

        /// <summary>Create the world. Safe to call once; repeated calls are ignored.</summary>
        public void Build()
        {
            if (_built)
            {
                return;
            }

            _built = true;
            _setupPanel = FindAnyObjectByType<StreamSetupPanel>();

            if (playerCamera == null && _setupPanel != null)
            {
                playerCamera = _setupPanel.sceneCamera;
            }

            if (playerCamera == null)
            {
                playerCamera = Camera.main;
            }

            if (playerCamera == null)
            {
                playerCamera = FindAnyObjectByType<Camera>();
            }

            BuildGround();
            BuildCity();
            BuildPlayer();
            BuildNetwork();
            BuildHud();

            if (playerCamera != null)
            {
                // Render the skybox instead of a flat colour; fall back to the dark clear
                // colour when the scene has no skybox material assigned.
                if (RenderSettings.skybox != null)
                {
                    playerCamera.clearFlags = CameraClearFlags.Skybox;
                }
                else
                {
                    playerCamera.clearFlags = CameraClearFlags.SolidColor;
                }

                playerCamera.backgroundColor = new Color(0.055f, 0.067f, 0.082f, 1f);
                playerCamera.nearClipPlane = 0.1f;
                playerCamera.farClipPlane = 600f;
            }

            if (gameplayCanvas == null)
            {
                gameplayCanvas = GameObject.Find("FXM Canvas");
            }

            if (gameplayCanvas != null)
            {
                _canvasVisibleInWatch = gameplayCanvas.activeSelf;
            }
        }

        private void UpdateGameplayCanvas()
        {
            if (gameplayCanvas == null)
            {
                return;
            }

            // Stream mode is gameplay: the uGUI playback canvas would sit on top of the
            // world, so hide it and let the camera (and the stream preview) show the scene.
            bool gameplay = _setupPanel == null ||
                            _setupPanel.Mode == StreamSetupPanel.SetupMode.Stream;
            bool show = !gameplay && _canvasVisibleInWatch;

            if (gameplayCanvas.activeSelf != show)
            {
                gameplayCanvas.SetActive(show);
            }
        }

        private void BuildGround()
        {
            GameObject ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "Walking Sim Ground";
            ground.transform.position = new Vector3(spawnPoint.x, groundHeight - 0.02f, spawnPoint.z);
            ground.transform.localScale = new Vector3(30f, 1f, 30f);

            var renderer = ground.GetComponent<Renderer>();
            if (renderer != null)
            {
                Shader shader = Shader.Find("Universal Render Pipeline/Lit");
                if (shader == null)
                {
                    shader = Shader.Find("Standard");
                }

                if (shader != null)
                {
                    _groundMaterial = new Material(shader)
                    {
                        name = "Walking Sim Ground",
                        color = new Color(0.075f, 0.085f, 0.095f, 1f)
                    };
                    renderer.sharedMaterial = _groundMaterial;
                }

                renderer.shadowCastingMode = ShadowCastingMode.Off;
                renderer.receiveShadows = true;
            }
        }

        private void BuildCity()
        {
            if (cityPrefab == null)
            {
                Debug.LogWarning("[FXM.WalkingSim] No city prefab assigned; only the ground was built.");
                return;
            }

            _city = Instantiate(cityPrefab);
            _city.name = "LowPolyCity (walking sim)";
            _city.transform.position = cityPrefab.transform.position;
            _city.transform.rotation = cityPrefab.transform.rotation;

            if (_city.GetComponent<AudioListener>() != null)
            {
                Destroy(_city.GetComponent<AudioListener>());
            }

            if (!addCityColliders)
            {
                return;
            }

            // The prefab contains one empty MeshCollider; remove it and give the visible
            // building meshes real colliders. Flat road pieces are skipped because the
            // ground plane already handles walking.
            var emptyColliders = _city.GetComponentsInChildren<MeshCollider>(true);
            for (int i = 0; i < emptyColliders.Length; i++)
            {
                if (emptyColliders[i] != null && emptyColliders[i].sharedMesh == null)
                {
                    Destroy(emptyColliders[i]);
                }
            }

            var renderers = _city.GetComponentsInChildren<MeshRenderer>(true);
            int added = 0;

            for (int i = 0; i < renderers.Length; i++)
            {
                MeshRenderer renderer = renderers[i];
                if (renderer == null || renderer.GetComponent<Collider>() != null)
                {
                    continue;
                }

                MeshFilter filter = renderer.GetComponent<MeshFilter>();
                if (filter == null || filter.sharedMesh == null)
                {
                    continue;
                }

                if (renderer.bounds.size.y < 1f || filter.sharedMesh.vertexCount > 60000)
                {
                    continue;
                }

                var collider = renderer.gameObject.AddComponent<MeshCollider>();
                collider.sharedMesh = filter.sharedMesh;
                collider.convex = false;
                added++;
            }

            Debug.Log("[FXM.WalkingSim] City ready with " + added + " solid mesh colliders.");
        }

        private void BuildPlayer()
        {
            if (guyPrefab == null)
            {
                Debug.LogError("[FXM.WalkingSim] No Guy prefab assigned; the game cannot start.");
                return;
            }

            var avatar = Instantiate(guyPrefab, spawnPoint, Quaternion.identity);
            avatar.name = "Player";

            var animator = avatar.GetComponent<Animator>();
            if (animator == null)
            {
                animator = avatar.AddComponent<Animator>();
            }

            animator.applyRootMotion = false;
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;

            var guyAnimator = avatar.AddComponent<GuyAnimator>();
            guyAnimator.Configure(idleClip, walkClip);

            _player = avatar.AddComponent<WalkingSimPlayer>();
            _player.Configure(playerCamera, _setupPanel, walkSpeed, cameraDistance, lookSensitivity);
        }

        private void BuildNetwork()
        {
            if (_player == null)
            {
                return;
            }

            _network = gameObject.GetComponent<WalkingSimNetwork>();
            if (_network == null)
            {
                _network = gameObject.AddComponent<WalkingSimNetwork>();
            }

            _network.Configure(_player, _setupPanel, guyPrefab, idleClip, walkClip, spawnPoint);
            _network.relayUrl = relayUrl;
            _network.room = gameRoom;
            _network.token = relayToken;
        }

        private void BuildHud()
        {
            var hud = gameObject.GetComponent<WalkingSimHud>();
            if (hud == null)
            {
                hud = gameObject.AddComponent<WalkingSimHud>();
            }

            hud.Configure(_player, _network, _setupPanel);
        }

        private void OnDestroy()
        {
            if (_groundMaterial != null)
            {
                Destroy(_groundMaterial);
            }
        }
    }
}
