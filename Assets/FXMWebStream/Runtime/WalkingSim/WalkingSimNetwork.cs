using System;
using System.Collections.Generic;
using FXM.WebStream;
using UnityEngine;

namespace FXM.WebStream.WalkingSim
{
    /// <summary>
    /// Shares the local player transform with everyone in the same relay room and spawns
    /// a Guy avatar for each remote player. Remote avatars are solid capsules, so players
    /// cannot walk through each other. State uses role=player on the relay; video streams
    /// keep their own sender/receiver roles and are not affected.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("FXM WebStream/Walking Sim/Walking Sim Network")]
    public sealed class WalkingSimNetwork : MonoBehaviour
    {
        [Header("Relay")]
        public string relayUrl = "ws://127.0.0.1:8787/fxm-relay";
        public string room = "demo";
        public string token = string.Empty;

        [Tooltip("Seconds between position updates. 10 is smooth with very little bandwidth.")]
        [Range(4f, 30f)]
        public float updatesPerSecond = 10f;

        [Tooltip("Seconds before a silent remote player is removed.")]
        [Range(2f, 20f)]
        public float remoteTimeout = 5f;

        [Header("Spawning")]
        [Tooltip("Shared spawn point. Each player starts on a small ring around it so nobody spawns inside another avatar.")]
        public Vector3 spawnOrigin = new Vector3(-61.17f, 16.9f, -76.66f);

        [Range(0f, 6f)]
        public float spawnRingRadius = 2.2f;

        [Header("Avatars")]
        [Tooltip("Usually Assets/Prefabs/Guy.fbx.")]
        public GameObject avatarPrefab;

        [Tooltip("Idle clip from the Guy model.")]
        public AnimationClip idleClip;

        [Tooltip("Walk clip from the Guy model.")]
        public AnimationClip walkClip;

        private readonly Dictionary<uint, RemotePlayerAvatar> _remotes =
            new Dictionary<uint, RemotePlayerAvatar>();

        private readonly object _statusLock = new object();
        private WalkingSimPlayer _localPlayer;
        private StreamSetupPanel _setupPanel;
        private WebSocketTransport _transport;
        private uint _localId;
        private uint _sequence;
        private float _sendTimer;
        private float _cleanupTimer;
        private string _status = "Multiplayer waiting for a stream.";
        private bool _started;
        private bool _warnedNoAvatar;

        public int PlayerCount
        {
            get { return 1 + _remotes.Count; }
        }

        public bool IsConnected
        {
            get { return _transport != null && _transport.IsRunning && _transport.PeerCount > 0; }
        }

        public string Status
        {
            get
            {
                lock (_statusLock)
                {
                    return _status;
                }
            }
        }

        public uint LocalPlayerId
        {
            get { return _localId; }
        }

        public void Configure(WalkingSimPlayer player, StreamSetupPanel panel, GameObject prefab,
            AnimationClip idle, AnimationClip walk, Vector3 spawnPoint)
        {
            _localPlayer = player;
            _setupPanel = panel;
            avatarPrefab = prefab;
            idleClip = idle;
            walkClip = walk;
            spawnOrigin = spawnPoint;
        }

        private void Start()
        {
            if (_setupPanel == null)
            {
                _setupPanel = FindAnyObjectByType<StreamSetupPanel>();
            }

            _localId = CreatePlayerId();
            PlaceLocalPlayerAtSpawn();

            if (_setupPanel == null)
            {
                StartNetwork();
            }
        }

        private void Update()
        {
            if (_setupPanel != null)
            {
                bool shouldConnect = _setupPanel.Mode == StreamSetupPanel.SetupMode.Stream &&
                                     _setupPanel.IsStarted;

                if (shouldConnect && !_started)
                {
                    StartNetwork();
                }
                else if (!shouldConnect && _started)
                {
                    StopNetwork();
                }
            }
            else if (!_started)
            {
                StartNetwork();
            }

            if (_transport == null)
            {
                return;
            }

            _transport.Poll();

            if (!_transport.IsRunning)
            {
                return;
            }

            _sendTimer += Time.unscaledDeltaTime;
            float interval = 1f / Mathf.Max(1f, updatesPerSecond);
            if (_sendTimer >= interval)
            {
                _sendTimer = 0f;
                SendLocalState();
            }

            _cleanupTimer += Time.unscaledDeltaTime;
            if (_cleanupTimer >= 1f)
            {
                _cleanupTimer = 0f;
                RemoveTimedOutPlayers();
            }
        }

        private void StartNetwork()
        {
            if (_started)
            {
                return;
            }

            if (avatarPrefab == null)
            {
                if (!_warnedNoAvatar)
                {
                    _warnedNoAvatar = true;
                    Debug.LogWarning("[FXM.WalkingSim] No Guy prefab assigned; remote players cannot be shown.");
                }

                return;
            }

            _started = true;
            _transport = new WebSocketTransport();
            _transport.FrameReceived += OnFrameReceived;
            _transport.Log += OnTransportLog;

            var config = TransportConfig.CreateRelay(relayUrl, room, token, RelayPeerRole.Player);
            config.SendQueueCapacity = 8;
            config.AutoReconnect = true;

            SetStatus("Multiplayer connecting to " + room + "...");
            if (!_transport.Start(config))
            {
                SetStatus("Multiplayer could not start. Check the relay URL.");
                Debug.LogWarning("[FXM.WalkingSim] Could not start the player relay connection.");
            }
            else
            {
                Debug.Log("[FXM.WalkingSim] Joining relay room '" + room +
                          "' as a player. Open the same room on other devices to meet them.");
            }
        }

        private void StopNetwork()
        {
            _started = false;

            if (_transport != null)
            {
                _transport.FrameReceived -= OnFrameReceived;
                _transport.Log -= OnTransportLog;
                _transport.Dispose();
                _transport = null;
            }

            ClearRemotes();
            SetStatus("Multiplayer stopped.");
        }

        private void PlaceLocalPlayerAtSpawn()
        {
            if (_localPlayer == null)
            {
                return;
            }

            Vector3 position = spawnOrigin;
            if (spawnRingRadius > 0.01f)
            {
                // A stable slot per id keeps players from sharing the exact same point
                // when several instances launch together.
                int slot = (int)(_localId % 16u);
                float angle = slot / 16f * Mathf.PI * 2f;
                position += new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * spawnRingRadius;
            }

            _localPlayer.Teleport(position);
        }

        private void SendLocalState()
        {
            if (_localPlayer == null || _transport == null)
            {
                return;
            }

            var state = new WalkingSimState
            {
                PlayerId = _localId,
                Sequence = ++_sequence,
                Position = _localPlayer.transform.position,
                Yaw = _localPlayer.transform.eulerAngles.y,
                Moving = _localPlayer.IsMoving
            };

            var buffer = new byte[WalkingSimProtocol.PacketSize];
            int length = WalkingSimProtocol.Write(buffer, 0, state);
            if (length > 0)
            {
                _transport.Send(new ArraySegment<byte>(buffer, 0, length));
            }
        }

        private void OnFrameReceived(ArraySegment<byte> data)
        {
            WalkingSimState state;
            if (!WalkingSimProtocol.TryRead(data, out state) || state.PlayerId == _localId)
            {
                return;
            }

            RemotePlayerAvatar remote;
            if (!_remotes.TryGetValue(state.PlayerId, out remote) || remote == null)
            {
                remote = CreateRemoteAvatar(state);
                if (remote == null)
                {
                    return;
                }
            }

            remote.Apply(state);
        }

        private RemotePlayerAvatar CreateRemoteAvatar(WalkingSimState state)
        {
            if (avatarPrefab == null)
            {
                return null;
            }

            GameObject avatar = Instantiate(avatarPrefab);
            avatar.name = "Remote Player " + state.PlayerId.ToString("X8");
            avatar.transform.position = state.Position;
            avatar.transform.rotation = Quaternion.Euler(0f, state.Yaw, 0f);

            // The raw model has no gameplay scripts, but a custom prefab might. Remote
            // players are driven by the network, never by local input or gravity.
            var controller = avatar.GetComponent<CharacterController>();
            if (controller != null)
            {
                Destroy(controller);
            }

            var colliders = avatar.GetComponentsInChildren<Collider>();
            for (int i = 0; i < colliders.Length; i++)
            {
                Destroy(colliders[i]);
            }

            var capsule = avatar.AddComponent<CapsuleCollider>();
            capsule.center = new Vector3(0f, 0.95f, 0f);
            capsule.height = 1.9f;
            capsule.radius = 0.32f;
            capsule.direction = 1;
            capsule.isTrigger = false;

            var animator = avatar.GetComponent<Animator>();
            if (animator == null)
            {
                animator = avatar.AddComponent<Animator>();
            }

            animator.applyRootMotion = false;
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;

            var guy = avatar.AddComponent<GuyAnimator>();
            guy.Configure(idleClip, walkClip);

            var remote = avatar.AddComponent<RemotePlayerAvatar>();
            remote.Configure(state.PlayerId, guy, state);

            _remotes[state.PlayerId] = remote;
            SetStatus("Multiplayer online | " + _remotes.Count + " other player(s).");
            return remote;
        }

        private void RemoveTimedOutPlayers()
        {
            if (_remotes.Count == 0)
            {
                return;
            }

            List<uint> expired = null;
            foreach (var pair in _remotes)
            {
                if (pair.Value == null || Time.unscaledTime - pair.Value.LastSeen > remoteTimeout)
                {
                    if (expired == null)
                    {
                        expired = new List<uint>();
                    }

                    expired.Add(pair.Key);
                }
            }

            if (expired == null)
            {
                return;
            }

            for (int i = 0; i < expired.Count; i++)
            {
                RemotePlayerAvatar remote;
                if (_remotes.TryGetValue(expired[i], out remote))
                {
                    _remotes.Remove(expired[i]);
                    if (remote != null)
                    {
                        Destroy(remote.gameObject);
                    }
                }
            }

            SetStatus("Multiplayer online | " + _remotes.Count + " other player(s).");
        }

        private void ClearRemotes()
        {
            foreach (var pair in _remotes)
            {
                if (pair.Value != null)
                {
                    Destroy(pair.Value.gameObject);
                }
            }

            _remotes.Clear();
        }

        private void OnTransportLog(string message)
        {
            if (string.IsNullOrEmpty(message))
            {
                return;
            }

            if (message.IndexOf("connected", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                SetStatus("Multiplayer online. Walk near another player to collide.");
            }
            else if (message.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     message.IndexOf("failed", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     message.IndexOf("refused", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                SetStatus("Relay offline. Start Tools/RelayServer or check the URL.");
            }
        }

        private void SetStatus(string value)
        {
            lock (_statusLock)
            {
                _status = value;
            }
        }

        private static uint CreatePlayerId()
        {
            uint value = (uint)UnityEngine.Random.Range(1, int.MaxValue);
            value ^= (uint)(DateTime.UtcNow.Ticks & 0xFFFFFFFF);
            return value == 0 ? 1u : value;
        }

        private void OnDestroy()
        {
            StopNetwork();
        }
    }

    /// <summary>Interpolates one remote player and drives their idle/walk animation.</summary>
    [DisallowMultipleComponent]
    public sealed class RemotePlayerAvatar : MonoBehaviour
    {
        public static readonly List<RemotePlayerAvatar> Active = new List<RemotePlayerAvatar>();

        public uint PlayerId { get; private set; }
        public float LastSeen { get; private set; }

        private GuyAnimator _animator;
        private Vector3 _targetPosition;
        private float _targetYaw;
        private bool _targetMoving;

        public void Configure(uint playerId, GuyAnimator animator, in WalkingSimState state)
        {
            PlayerId = playerId;
            _animator = animator;
            _targetPosition = state.Position;
            _targetYaw = state.Yaw;
            _targetMoving = state.Moving;
            LastSeen = Time.unscaledTime;
            ApplyPose(true);
        }

        private void OnEnable()
        {
            if (!Active.Contains(this))
            {
                Active.Add(this);
            }
        }

        private void OnDisable()
        {
            Active.Remove(this);
        }

        public void Apply(in WalkingSimState state)
        {
            if (Vector3.Distance(_targetPosition, state.Position) > 8f)
            {
                _targetPosition = state.Position;
                _targetYaw = state.Yaw;
                ApplyPose(true);
            }
            else
            {
                _targetPosition = state.Position;
                _targetYaw = state.Yaw;
            }

            _targetMoving = state.Moving;
            LastSeen = Time.unscaledTime;
        }

        private void Update()
        {
            ApplyPose(false);

            if (_animator != null)
            {
                _animator.SetMoving(_targetMoving);
            }
        }

        private void ApplyPose(bool snap)
        {
            if (snap)
            {
                transform.position = _targetPosition;
                transform.rotation = Quaternion.Euler(0f, _targetYaw, 0f);
                return;
            }

            float t = 1f - Mathf.Exp(-14f * Time.deltaTime);
            transform.position = Vector3.Lerp(transform.position, _targetPosition, t);
            transform.rotation = Quaternion.Slerp(transform.rotation,
                Quaternion.Euler(0f, _targetYaw, 0f), t);
        }
    }
}
