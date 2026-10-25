using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace FXM.WebStream.WalkingSim
{
    /// <summary>
    /// Blends the Guy model's idle and walk clips without an AnimatorController asset.
    /// The clips are cloned at runtime so loop settings do not modify the imported FBX.
    /// Works for both the local player and remote avatars.
    /// </summary>
    [RequireComponent(typeof(Animator))]
    [AddComponentMenu("FXM WebStream/Walking Sim/Guy Animator")]
    public sealed class GuyAnimator : MonoBehaviour
    {
        [Tooltip("Idle clip. Usually Rig|idle from Assets/Prefabs/Guy.fbx.")]
        public AnimationClip idleClip;

        [Tooltip("Walk clip. Usually Rig|walk from Assets/Prefabs/Guy.fbx.")]
        public AnimationClip walkClip;

        [Tooltip("Seconds used to crossfade between idle and walk.")]
        public float blendSeconds = 0.16f;

        private Animator _animator;
        private PlayableGraph _graph;
        private AnimationMixerPlayable _mixer;
        private AnimationClipPlayable _idlePlayable;
        private AnimationClipPlayable _walkPlayable;
        private AnimationClip _idleInstance;
        private AnimationClip _walkInstance;
        private float _walkWeight;
        private bool _moving;
        private float _speed = 3.2f;
        private bool _configured;
        private bool _graphRunning;

        public bool IsReady { get { return _graphRunning; } }

        /// <summary>0 = idle, 1 = full walk. Useful for HUDs and tests.</summary>
        public float WalkWeight { get { return _walkWeight; } }

        public void Configure(AnimationClip idle, AnimationClip walk)
        {
            idleClip = idle;
            walkClip = walk;
            _configured = true;

            if (isActiveAndEnabled && Application.isPlaying)
            {
                BuildGraph();
            }
        }

        public void SetMoving(bool moving, float speed = 3.2f)
        {
            _moving = moving;
            if (speed > 0.01f)
            {
                _speed = speed;
            }
        }

        private void Awake()
        {
            _animator = GetComponent<Animator>();
            _animator.applyRootMotion = false;
            _animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
        }

        private void Start()
        {
            if (!_configured)
            {
                _configured = idleClip != null || walkClip != null;
            }

            BuildGraph();
        }

        private void OnEnable()
        {
            if (_graph.IsValid() && !_graphRunning)
            {
                _graph.Play();
                _graphRunning = true;
            }
        }

        private void OnDisable()
        {
            if (_graph.IsValid() && _graphRunning)
            {
                _graph.Stop();
                _graphRunning = false;
            }
        }

        private void Update()
        {
            if (!_graphRunning)
            {
                return;
            }

            float target = _moving ? 1f : 0f;
            float step = blendSeconds > 0.001f ? Time.deltaTime / blendSeconds : 1f;
            _walkWeight = Mathf.MoveTowards(_walkWeight, target, step);

            if (_mixer.IsValid())
            {
                _mixer.SetInputWeight(0, 1f - _walkWeight);
                _mixer.SetInputWeight(1, _walkWeight);
            }

            if (_walkPlayable.IsValid())
            {
                _walkPlayable.SetSpeed(Mathf.Clamp(_speed / 3.2f, 0.7f, 1.5f));
            }
        }

        private void OnDestroy()
        {
            if (_graph.IsValid())
            {
                _graph.Destroy();
            }

            DestroyClip(_idleInstance);
            DestroyClip(_walkInstance);
            _idleInstance = null;
            _walkInstance = null;
        }

        private void BuildGraph()
        {
            if (_graphRunning || _animator == null || (idleClip == null && walkClip == null))
            {
                return;
            }

            _idleInstance = PrepareClip(idleClip);
            _walkInstance = PrepareClip(walkClip);

            _graph = PlayableGraph.Create("FXM Guy Animation");
            _graph.SetTimeUpdateMode(DirectorUpdateMode.GameTime);

            var output = AnimationPlayableOutput.Create(_graph, "Guy", _animator);
            _mixer = AnimationMixerPlayable.Create(_graph, 2);

            if (_idleInstance != null)
            {
                _idlePlayable = AnimationClipPlayable.Create(_graph, _idleInstance);
                _graph.Connect(_idlePlayable, 0, _mixer, 0);
            }

            if (_walkInstance != null)
            {
                _walkPlayable = AnimationClipPlayable.Create(_graph, _walkInstance);
                _graph.Connect(_walkPlayable, 0, _mixer, 1);
            }

            _mixer.SetInputWeight(0, 1f);
            _mixer.SetInputWeight(1, 0f);
            output.SetSourcePlayable(_mixer);
            _graph.Play();
            _graphRunning = true;
            _walkWeight = 0f;
        }

        private static AnimationClip PrepareClip(AnimationClip source)
        {
            if (source == null)
            {
                return null;
            }

            AnimationClip clip = Object.Instantiate(source);
            clip.name = source.name + " (runtime)";
            clip.wrapMode = WrapMode.Loop;
            clip.legacy = false;
            return clip;
        }

        private static void DestroyClip(AnimationClip clip)
        {
            if (clip != null)
            {
                Object.Destroy(clip);
            }
        }
    }
}
