using FXM.WebStream.WalkingSim;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace FXM.WebStream.EditorTools
{
    /// <summary>
    /// One-click conversion of the demo scene: removes the rotating cube, adds a walking
    /// sim host, and wires the Guy idle/walk clips and the city prefab.
    /// </summary>
    public static class WalkingSimSceneWizard
    {
        private const string GuyPath = "Assets/Prefabs/Guy.fbx";
        private const string CityPath = "Assets/Prefabs/LowPolyCity.prefab";

        [MenuItem("Tools/FXM WebStream/Replace Test Cube with Walking Sim")]
        public static void ReplaceTestCube()
        {
            if (!EditorUtility.DisplayDialog("FXM WebStream",
                    "Replace the rotating test cube with a playable walking sim?\n\n" +
                    "The city prefab, Guy model, third-person camera, relay player room and " +
                    "touch controls are wired automatically.", "Replace", "Cancel"))
            {
                return;
            }

            Apply();
        }

        /// <summary>Batch entry point: open the demo scene, then wire the walking sim.</summary>
        public static void ApplyToDemoScene()
        {
            EditorSceneManager.OpenScene("Assets/Scenes/FXMWebStream.unity", OpenSceneMode.Single);
            Apply();
        }

        /// <summary>Build or update the walking sim in the active scene. Used by batch mode too.</summary>
        public static void Apply()
        {
            Scene scene = SceneManager.GetActiveScene();
            if (!scene.IsValid() || !scene.isLoaded)
            {
                Debug.LogError("[FXM.WalkingSim] Open a scene before adding the walking sim.");
                return;
            }

            GameObject cube = GameObject.Find("FXM Test Cube");
            if (cube != null)
            {
                Undo.DestroyObjectImmediate(cube);
            }

            GameObject host = null;
            var existing = Object.FindAnyObjectByType<WalkingSimBootstrap>(FindObjectsInactive.Include);
            if (existing != null)
            {
                host = existing.gameObject;
            }

            if (host == null)
            {
                host = new GameObject("Walking Sim");
                Undo.RegisterCreatedObjectUndo(host, "Create Walking Sim");
            }

            var bootstrap = host.GetComponent<WalkingSimBootstrap>();
            if (bootstrap == null)
            {
                bootstrap = Undo.AddComponent<WalkingSimBootstrap>(host);
            }

            GameObject guy = AssetDatabase.LoadAssetAtPath<GameObject>(GuyPath);
            GameObject city = AssetDatabase.LoadAssetAtPath<GameObject>(CityPath);
            AnimationClip idle = null;
            AnimationClip walk = null;

            Object[] assets = AssetDatabase.LoadAllAssetsAtPath(GuyPath);
            for (int i = 0; i < assets.Length; i++)
            {
                var clip = assets[i] as AnimationClip;
                if (clip == null || clip.name.StartsWith("__preview__"))
                {
                    continue;
                }

                if (clip.name.EndsWith("|idle"))
                {
                    idle = clip;
                }
                else if (clip.name.EndsWith("|walk"))
                {
                    walk = clip;
                }
            }

            Camera camera = Camera.main;
            if (camera == null)
            {
                camera = Object.FindAnyObjectByType<Camera>();
            }

            var panel = Object.FindAnyObjectByType<StreamSetupPanel>(FindObjectsInactive.Include);

            Undo.RecordObject(bootstrap, "Configure Walking Sim");
            bootstrap.guyPrefab = guy;
            bootstrap.cityPrefab = city;
            bootstrap.idleClip = idle;
            bootstrap.walkClip = walk;
            bootstrap.playerCamera = camera;
            bootstrap.spawnPoint = new Vector3(-61.17f, 16.9f, -76.66f);
            bootstrap.groundHeight = 16.7f;
            bootstrap.addCityColliders = true;
            bootstrap.walkSpeed = 3.2f;
            bootstrap.cameraDistance = 4f;
            bootstrap.lookSensitivity = 0.11f;
            bootstrap.autoHideSetupPanel = true;

            GameObject gameplayCanvas = GameObject.Find("FXM Canvas");
            if (gameplayCanvas == null)
            {
                Canvas anyCanvas = Object.FindAnyObjectByType<Canvas>(FindObjectsInactive.Include);
                gameplayCanvas = anyCanvas != null ? anyCanvas.gameObject : null;
            }

            bootstrap.gameplayCanvas = gameplayCanvas;
            bootstrap.relayUrl = "ws://127.0.0.1:8787/fxm-relay";
            bootstrap.gameRoom = "demo";
            bootstrap.relayToken = string.Empty;
            EditorUtility.SetDirty(bootstrap);

            if (panel != null)
            {
                Undo.RecordObject(panel, "Configure Stream Panel");
                panel.sceneCamera = camera;
                panel.fullscreenFill = false;
                EditorUtility.SetDirty(panel);
            }

            if (camera != null)
            {
                Undo.RecordObject(camera, "Configure Walking Camera");
                camera.clearFlags = RenderSettings.skybox != null
                    ? CameraClearFlags.Skybox
                    : CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(0.055f, 0.067f, 0.082f, 1f);
                camera.nearClipPlane = 0.1f;
                camera.farClipPlane = 600f;
                EditorUtility.SetDirty(camera);
            }

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Selection.activeGameObject = host;

            Debug.Log("[FXM.WalkingSim] Walking sim ready. Clips: " +
                      (idle != null ? idle.name : "missing idle") + ", " +
                      (walk != null ? walk.name : "missing walk") +
                      ". Press Play, then Hide or Start streaming to walk.");
        }
    }
}
