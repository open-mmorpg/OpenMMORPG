using UnityEngine;

namespace MultiplayerARPG.Demo
{
    /// <summary>
    /// Drives a mount's gait from how fast it is actually travelling.
    ///
    /// `VehicleEntity` carries no model and no animator — it is seats, speed and hit
    /// points, and nothing in the kit ever touches an `Animator` on it. So a mount
    /// imported with walk and gallop clips just slides around the map until something
    /// plays them, and this is that something.
    ///
    /// It reads **measured** speed rather than asking the entity for its move speed.
    /// `GetMoveSpeed` returns what the vehicle is *allowed* to do, not what it is doing,
    /// so a horse standing still would gallop on the spot. Position delta is also the
    /// only thing that works unchanged on a remote client, where the vehicle is moved by
    /// the sync layer rather than by its own movement component.
    ///
    /// Attach to the model, beside the `Animator`. See <see cref="EditorTools.DemoMountBuilder"/>.
    /// </summary>
    [RequireComponent(typeof(Animator))]
    public class DemoMountAnimator : MonoBehaviour
    {
        private static readonly int SpeedHash = Animator.StringToHash("Speed");

        [Tooltip("Transform whose motion is measured. Defaults to the entity root above this model.")]
        [SerializeField]
        private Transform motionSource = null;

        [Tooltip("Seconds of smoothing on the measured speed. Keeps the blend from " +
                 "flickering between gaits when the controller stutters for a frame.")]
        [SerializeField]
        private float smoothing = 0.12f;

        private Animator _animator;
        private Vector3 _previousPosition;
        private float _speed;

        private void Awake()
        {
            _animator = GetComponent<Animator>();
            if (motionSource == null)
                motionSource = transform.parent != null ? transform.parent : transform;
            _previousPosition = motionSource.position;
        }

        private void OnEnable()
        {
            // Without this a mount that was pooled and respawned elsewhere measures the
            // whole teleport as one frame of movement, and bursts into a gallop on spawn.
            _previousPosition = motionSource.position;
            _speed = 0f;
        }

        private void Update()
        {
            if (Time.deltaTime <= 0f)
                return;

            Vector3 position = motionSource.position;
            Vector3 delta = position - _previousPosition;
            _previousPosition = position;

            // Vertical motion is falling and terrain, not gait.
            delta.y = 0f;
            float measured = delta.magnitude / Time.deltaTime;

            _speed = smoothing > 0f
                ? Mathf.Lerp(_speed, measured, 1f - Mathf.Exp(-Time.deltaTime / smoothing))
                : measured;

            _animator.SetFloat(SpeedHash, _speed);
        }
    }
}
