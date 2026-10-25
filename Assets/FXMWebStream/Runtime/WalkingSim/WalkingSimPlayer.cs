using UnityEngine;

#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace FXM.WebStream.WalkingSim
{
    /// <summary>
    /// Third-person controller for the Guy model. Desktop uses WASD and free mouse look;
    /// phones and tablets use the on-screen joystick plus a swipe anywhere on screen.
    /// Input is ignored while the setup panel is open so typing in the panel never walks
    /// the character.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("FXM WebStream/Walking Sim/Walking Sim Player")]
    public sealed class WalkingSimPlayer : MonoBehaviour
    {
        [Header("Movement")]
        [Tooltip("Walking speed in metres per second.")]
        public float walkSpeed = 3.2f;

        [Tooltip("How quickly the character reaches the requested speed.")]
        public float acceleration = 24f;

        [Tooltip("Gravity applied by the CharacterController.")]
        public float gravity = -22f;

        [Header("Camera")]
        [Tooltip("Camera used for the third-person follow view and by the stream sender.")]
        public Camera followCamera;

        [Tooltip("Distance behind the character.")]
        public float cameraDistance = 4f;

        [Tooltip("Height of the point the camera looks at.")]
        public float cameraFocusHeight = 1.45f;

        [Tooltip("Pitch in degrees. Positive looks down at the character.")]
        [Range(-15f, 65f)]
        public float cameraPitch = 16f;

        [Tooltip("Mouse look speed while playing.")]
        public float mouseSensitivity = 0.11f;

        [Tooltip("Touch look speed while swiping outside the analogue stick.")]
        public float touchSensitivity = 0.16f;

        [Header("Touch controls")]
        [Tooltip("Show the analogue stick in the Editor/desktop for testing. It is always shown on phones.")]
        public bool forceTouchControls;

        [Tooltip("Extra size multiplier for the analogue stick.")]
        [Range(0.6f, 1.6f)]
        public float touchControlScale = 1f;

        private CharacterController _controller;
        private GuyAnimator _guyAnimator;
        private StreamSetupPanel _setupPanel;
        private Transform _cameraTransform;
        private Vector3 _planarVelocity;
        private float _verticalVelocity;
        private float _yaw;
        private float _pitch;

        private bool _inputReady;
        private Vector2 _touchMove;
        private int _joystickTouchId = -1;
        private int _lookTouchId = -1;
        private Vector2 _lookLastPosition;
        private Vector2 _joystickCenter;
        private float _joystickRadius = 80f;

        private Texture2D _joystickBaseTexture;
        private Texture2D _joystickKnobTexture;
        private GUIStyle _joystickLabelStyle;

        public bool IsMoving
        {
            get { return _planarVelocity.sqrMagnitude > 0.04f; }
        }

        public void Configure(Camera camera, StreamSetupPanel panel, float speed,
            float distance, float sensitivity)
        {
            followCamera = camera;
            _setupPanel = panel;
            walkSpeed = Mathf.Max(0.5f, speed);
            cameraDistance = Mathf.Max(1.5f, distance);
            mouseSensitivity = Mathf.Max(0.01f, sensitivity);
            _inputReady = true;
        }

        private void Awake()
        {
            _controller = GetComponent<CharacterController>();
            _guyAnimator = GetComponent<GuyAnimator>();
            EnsureController();
            EnsureJoystickTextures();
        }

        private void Start()
        {
            if (followCamera == null)
            {
                followCamera = Camera.main;
            }

            if (_setupPanel == null)
            {
                _setupPanel = FindAnyObjectByType<StreamSetupPanel>();
            }

            if (_guyAnimator == null)
            {
                _guyAnimator = GetComponent<GuyAnimator>();
            }

            _cameraTransform = followCamera != null ? followCamera.transform : null;
            _yaw = transform.eulerAngles.y;
            _pitch = Mathf.Clamp(cameraPitch, -15f, 65f);

            if (_cameraTransform != null)
            {
                ApplyCamera(1f);
            }
        }

        private void Update()
        {
            if (!_inputReady)
            {
                return;
            }

            bool canControl = CanControl();
            Vector2 input = Vector2.zero;

            if (canControl)
            {
                ReadKeyboard(ref input);

                if (IsTouchControlVisible)
                {
                    ReadTouches();
                    if (_touchMove.sqrMagnitude > 0.001f)
                    {
                        input = _touchMove;
                    }
                }
                else
                {
                    ResetTouches();
                }
            }
            else
            {
                ResetTouches();
            }

            MoveCharacter(input, canControl);
            ResolveRemoteContacts();
        }

        /// <summary>Move the player without letting the CharacterController interpolate through walls.</summary>
        public void Teleport(Vector3 position)
        {
            if (_controller != null)
            {
                _controller.enabled = false;
                transform.position = position;
                _controller.enabled = true;
            }
            else
            {
                transform.position = position;
            }

            _planarVelocity = Vector3.zero;
            _verticalVelocity = 0f;
        }

        private void LateUpdate()
        {
            if (_cameraTransform == null)
            {
                return;
            }

            ApplyCamera(1f - Mathf.Exp(-12f * Time.deltaTime));
        }

        private bool CanControl()
        {
            return _setupPanel == null || !_setupPanel.isActiveAndEnabled || _setupPanel.IsPanelHidden;
        }

        private static bool HasTouchInput
        {
            get
            {
#if ENABLE_INPUT_SYSTEM
                if (Touchscreen.current != null)
                {
                    return true;
                }
#elif ENABLE_LEGACY_INPUT_MANAGER
                if (Input.touchSupported)
                {
                    return true;
                }
#endif
                // Mobile browsers report WebGL as the platform, so also trust the runtime
                // check above; Application.isMobilePlatform keeps native phones covered.
                return Application.isMobilePlatform;
            }
        }

        private bool IsTouchControlVisible
        {
            get
            {
                return (HasTouchInput || forceTouchControls) &&
                       (_setupPanel == null || !_setupPanel.isActiveAndEnabled || _setupPanel.IsPanelHidden);
            }
        }

        private void ReadKeyboard(ref Vector2 input)
        {
#if ENABLE_INPUT_SYSTEM
            Keyboard keyboard = Keyboard.current;
            if (keyboard != null)
            {
                float x = 0f;
                float y = 0f;
                if (keyboard.aKey.isPressed || keyboard.leftArrowKey.isPressed) x -= 1f;
                if (keyboard.dKey.isPressed || keyboard.rightArrowKey.isPressed) x += 1f;
                if (keyboard.sKey.isPressed || keyboard.downArrowKey.isPressed) y -= 1f;
                if (keyboard.wKey.isPressed || keyboard.upArrowKey.isPressed) y += 1f;
                input = new Vector2(x, y);
            }

            Mouse mouse = Mouse.current;
            if (mouse != null)
            {
                Vector2 delta = mouse.delta.ReadValue();
                _yaw += delta.x * mouseSensitivity;
                _pitch = Mathf.Clamp(_pitch - delta.y * mouseSensitivity, -15f, 65f);
            }
#elif ENABLE_LEGACY_INPUT_MANAGER
            input = new Vector2(Input.GetAxisRaw("Horizontal"), Input.GetAxisRaw("Vertical"));
            _yaw += Input.GetAxis("Mouse X") * mouseSensitivity * 10f;
            _pitch = Mathf.Clamp(_pitch - Input.GetAxis("Mouse Y") * mouseSensitivity * 10f, -15f, 65f);
#endif
        }

        private void ReadTouches()
        {
            Rect safe = Screen.safeArea;
            float radius = Mathf.Clamp(Mathf.Min(safe.width, safe.height) * 0.115f, 50f, 88f) * touchControlScale;
            _joystickRadius = radius;
            _joystickCenter = new Vector2(safe.x + radius + 26f, safe.y + radius + 26f);

#if ENABLE_INPUT_SYSTEM
            Touchscreen touchscreen = Touchscreen.current;
            if (touchscreen == null)
            {
                return;
            }

            var touches = touchscreen.touches;
            for (int i = 0; i < touches.Count; i++)
            {
                var touch = touches[i];
                int id = touch.touchId.ReadValue();
                Vector2 position = touch.position.ReadValue();
                UnityEngine.InputSystem.TouchPhase phase = touch.phase.ReadValue();
                HandleTouch(id, position, phase, safe);
            }
#elif ENABLE_LEGACY_INPUT_MANAGER
            for (int i = 0; i < Input.touchCount; i++)
            {
                Touch touch = Input.GetTouch(i);
                HandleTouch(touch.fingerId, touch.position, ToPhase(touch.phase), safe);
            }
#endif
        }

#if ENABLE_INPUT_SYSTEM || ENABLE_LEGACY_INPUT_MANAGER
        private void HandleTouch(int id, Vector2 position,
            UnityEngine.InputSystem.TouchPhase phase, Rect safe)
        {
            bool began = phase == UnityEngine.InputSystem.TouchPhase.Began;
            bool ended = phase == UnityEngine.InputSystem.TouchPhase.Ended ||
                         phase == UnityEngine.InputSystem.TouchPhase.Canceled;

            if (began)
            {
                bool insideJoystick = Vector2.Distance(position, _joystickCenter) <= _joystickRadius * 1.75f;
                bool insideTopBar = position.y > Screen.height - 92f;

                if (insideJoystick && _joystickTouchId < 0)
                {
                    _joystickTouchId = id;
                    UpdateJoystick(position);
                }
                else if (!insideTopBar &&
                         position.y > safe.y + 24f &&
                         _lookTouchId < 0)
                {
                    // Any swipe outside the stick and the top bar looks around, so the
                    // player can steer with the right thumb anywhere on the screen.
                    _lookTouchId = id;
                    _lookLastPosition = position;
                }

                return;
            }

            if (id == _joystickTouchId)
            {
                if (ended)
                {
                    _joystickTouchId = -1;
                    _touchMove = Vector2.zero;
                }
                else
                {
                    UpdateJoystick(position);
                }

                return;
            }

            if (id == _lookTouchId)
            {
                if (ended)
                {
                    _lookTouchId = -1;
                }
                else
                {
                    Vector2 delta = position - _lookLastPosition;
                    _lookLastPosition = position;
                    _yaw += delta.x * touchSensitivity;
                    _pitch = Mathf.Clamp(_pitch - delta.y * touchSensitivity, -15f, 65f);
                }
            }
        }
#endif

        private void UpdateJoystick(Vector2 position)
        {
            Vector2 delta = position - _joystickCenter;
            Vector2 value = Vector2.ClampMagnitude(delta / Mathf.Max(1f, _joystickRadius), 1f);
            const float deadZone = 0.12f;
            float magnitude = value.magnitude;
            if (magnitude < deadZone)
            {
                _touchMove = Vector2.zero;
                return;
            }

            float remapped = Mathf.InverseLerp(deadZone, 1f, magnitude);
            _touchMove = value.normalized * remapped;
        }

        private void ResetTouches()
        {
            _joystickTouchId = -1;
            _lookTouchId = -1;
            _touchMove = Vector2.zero;
        }

        private void MoveCharacter(Vector2 input, bool canControl)
        {
            if (_controller == null)
            {
                return;
            }

            Vector3 desiredDirection = Vector3.zero;

            if (canControl && input.sqrMagnitude > 0.001f)
            {
                Vector3 forward = _cameraTransform != null
                    ? Vector3.ProjectOnPlane(_cameraTransform.forward, Vector3.up)
                    : transform.forward;
                if (forward.sqrMagnitude < 0.01f)
                {
                    forward = Vector3.forward;
                }

                forward.Normalize();
                Vector3 right = Vector3.Cross(Vector3.up, forward);
                desiredDirection = forward * Mathf.Clamp(input.y, -1f, 1f) +
                                   right * Mathf.Clamp(input.x, -1f, 1f);

                if (desiredDirection.sqrMagnitude > 1f)
                {
                    desiredDirection.Normalize();
                }

                if (desiredDirection.sqrMagnitude > 0.001f)
                {
                    Quaternion targetRotation = Quaternion.LookRotation(desiredDirection, Vector3.up);
                    transform.rotation = Quaternion.Slerp(transform.rotation, targetRotation,
                        1f - Mathf.Exp(-14f * Time.deltaTime));
                }
            }

            Vector3 targetVelocity = desiredDirection * walkSpeed;
            float rate = desiredDirection.sqrMagnitude > 0.001f ? acceleration : acceleration * 1.8f;
            _planarVelocity = Vector3.MoveTowards(_planarVelocity, targetVelocity, rate * Time.deltaTime);

            if (_controller.isGrounded && _verticalVelocity < 0f)
            {
                _verticalVelocity = -2f;
            }

            _verticalVelocity += gravity * Time.deltaTime;
            _controller.Move((_planarVelocity + Vector3.up * _verticalVelocity) * Time.deltaTime);

            if (_guyAnimator != null)
            {
                _guyAnimator.SetMoving(_planarVelocity.magnitude > 0.18f, walkSpeed);
            }
        }

        private void ResolveRemoteContacts()
        {
            if (_controller == null)
            {
                return;
            }

            var remotes = RemotePlayerAvatar.Active;
            for (int i = 0; i < remotes.Count; i++)
            {
                RemotePlayerAvatar remote = remotes[i];
                if (remote == null || !remote.isActiveAndEnabled)
                {
                    continue;
                }

                Vector3 delta = transform.position - remote.transform.position;
                delta.y = 0f;
                float distance = delta.magnitude;

                // Both characters use a 0.32 m capsule. A remote player who steps into
                // the local one gently separates them instead of clipping through.
                const float minimumDistance = 0.64f;
                if (distance >= minimumDistance)
                {
                    continue;
                }

                if (distance < 0.001f)
                {
                    delta = -transform.forward;
                    distance = 0.001f;
                }

                float overlap = Mathf.Min(minimumDistance - distance, 0.08f);
                _controller.Move(delta.normalized * overlap);
            }
        }

        private void ApplyCamera(float smoothing)
        {
            if (_cameraTransform == null)
            {
                return;
            }

            Vector3 focus = transform.position + Vector3.up * cameraFocusHeight;
            Quaternion orbit = Quaternion.Euler(_pitch, _yaw, 0f);
            Vector3 desired = focus - orbit * Vector3.forward * cameraDistance;

            if (smoothing >= 1f)
            {
                _cameraTransform.position = desired;
            }
            else
            {
                _cameraTransform.position = Vector3.Lerp(
                    _cameraTransform.position, desired, Mathf.Clamp01(smoothing));
            }

            Vector3 look = focus - _cameraTransform.position;
            if (look.sqrMagnitude > 0.0001f)
            {
                _cameraTransform.rotation = Quaternion.LookRotation(look, Vector3.up);
            }
        }

        private void EnsureController()
        {
            if (_controller == null)
            {
                _controller = gameObject.AddComponent<CharacterController>();
            }

            _controller.height = 1.9f;
            _controller.radius = 0.32f;
            _controller.center = new Vector3(0f, 0.95f, 0f);
            _controller.skinWidth = 0.02f;
            _controller.stepOffset = 0.35f;
            _controller.slopeLimit = 48f;
        }

        // ------------------------------------------------------------------
        // Touch HUD
        // ------------------------------------------------------------------

        private void OnGUI()
        {
            if (!IsTouchControlVisible || _joystickBaseTexture == null)
            {
                return;
            }

            Rect safe = Screen.safeArea;
            float radius = Mathf.Clamp(Mathf.Min(safe.width, safe.height) * 0.115f, 50f, 88f) * touchControlScale;
            Vector2 center = new Vector2(safe.x + radius + 26f, safe.y + radius + 26f);
            Vector2 guiCenter = new Vector2(center.x, Screen.height - center.y);

            float size = radius * 2f;
            Color previous = GUI.color;

            GUI.color = new Color(1f, 1f, 1f, 0.22f);
            GUI.DrawTexture(new Rect(guiCenter.x - radius, guiCenter.y - radius, size, size),
                _joystickBaseTexture, ScaleMode.StretchToFill, true);

            Vector2 knob = guiCenter + new Vector2(_touchMove.x, -_touchMove.y) * radius * 0.72f;
            float knobSize = radius * 0.9f;
            GUI.color = new Color(1f, 1f, 1f, 0.62f);
            GUI.DrawTexture(new Rect(knob.x - knobSize * 0.5f, knob.y - knobSize * 0.5f,
                knobSize, knobSize), _joystickKnobTexture, ScaleMode.StretchToFill, true);

            if (_joystickLabelStyle == null)
            {
                _joystickLabelStyle = new GUIStyle(GUI.skin.label)
                {
                    alignment = TextAnchor.MiddleCenter,
                    fontSize = Mathf.RoundToInt(Mathf.Clamp(radius * 0.19f, 10f, 16f)),
                    fontStyle = FontStyle.Bold
                };
                _joystickLabelStyle.normal.textColor = new Color(1f, 1f, 1f, 0.68f);
            }

            GUI.color = Color.white;
            GUI.Label(new Rect(guiCenter.x - radius, guiCenter.y + radius + 4f, size, 18f), "MOVE",
                _joystickLabelStyle);

            GUI.color = previous;
        }

        private void EnsureJoystickTextures()
        {
            if (_joystickBaseTexture == null)
            {
                _joystickBaseTexture = CreateCircleTexture(128, 1f, 0.18f);
            }

            if (_joystickKnobTexture == null)
            {
                _joystickKnobTexture = CreateCircleTexture(128, 0.96f, 0.5f);
            }
        }

        private static Texture2D CreateCircleTexture(int size, float inner, float outerAlpha)
        {
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                name = "FXM Touch Circle",
                hideFlags = HideFlags.HideAndDontSave,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };

            var pixels = new Color32[size * size];
            float radius = size * 0.5f;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = x + 0.5f - radius;
                    float dy = y + 0.5f - radius;
                    float distance = Mathf.Sqrt(dx * dx + dy * dy) / radius;
                    float alpha = distance <= inner
                        ? outerAlpha
                        : Mathf.Clamp01((1f - distance) / Mathf.Max(0.001f, 1f - inner)) * outerAlpha;
                    pixels[y * size + x] = new Color32(255, 255, 255, (byte)(alpha * 255f));
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            return texture;
        }

        private void OnDestroy()
        {
            if (_joystickBaseTexture != null)
            {
                Destroy(_joystickBaseTexture);
            }

            if (_joystickKnobTexture != null)
            {
                Destroy(_joystickKnobTexture);
            }
        }

#if ENABLE_LEGACY_INPUT_MANAGER && !ENABLE_INPUT_SYSTEM
        private static UnityEngine.InputSystem.TouchPhase ToPhase(TouchPhase phase)
        {
            switch (phase)
            {
                case TouchPhase.Began: return UnityEngine.InputSystem.TouchPhase.Began;
                case TouchPhase.Moved: return UnityEngine.InputSystem.TouchPhase.Moved;
                case TouchPhase.Stationary: return UnityEngine.InputSystem.TouchPhase.Stationary;
                case TouchPhase.Ended: return UnityEngine.InputSystem.TouchPhase.Ended;
                default: return UnityEngine.InputSystem.TouchPhase.Canceled;
            }
        }
#endif
    }
}
