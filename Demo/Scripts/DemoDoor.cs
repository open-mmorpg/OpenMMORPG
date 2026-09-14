using UnityEngine;

namespace MultiplayerARPG.Demo
{
    /// <summary>
    /// A house door that swings when a player opens it, and shuts itself a few seconds later.
    ///
    /// The door is solid once it has stopped moving, so it has to be opened before anyone
    /// can walk through. It swings inward, away from whoever is arriving rather than into
    /// their face.
    ///
    /// This is presentation only and runs on every client independently. A door is not worth
    /// a network message: the worst a disagreement can cost is one player seeing a door open
    /// that another sees shut, and either can open it for themselves.
    /// </summary>
    [DisallowMultipleComponent]
    public class DemoDoor : MonoBehaviour
    {
        [Tooltip("The transform that swings. Its pivot sits on the hinge.")]
        public Transform pivot;

        [Tooltip("How far the door swings, in degrees. Negative swings the other way.")]
        public float openAngle = 100f;

        [Tooltip("Degrees per second.")]
        public float swingSpeed = 260f;

        [Tooltip("How long it stays open before shutting itself, in seconds.")]
        public float openSeconds = 5f;

        /// <summary>
        /// Half the size of the box that decides whether anyone is standing in the doorway,
        /// in metres. A little wider than the opening and about as tall as a character.
        /// </summary>
        private static readonly Vector3 Threshold = new Vector3(0.6f, 1.0f, 0.5f);

        /// <summary>Whether the door is open, or on its way there.</summary>
        public bool IsOpen { get; private set; }

        /// <summary>Whether the leaf is moving right now.</summary>
        public bool IsSwinging { get; private set; }

        private Collider[] _leaf;
        private readonly Collider[] _inTheWay = new Collider[8];
        private float _angle;
        private float _closeAt;

        private void Reset()
        {
            pivot = transform;
        }

        private void Awake()
        {
            _leaf = pivot != null ? pivot.GetComponentsInChildren<Collider>() : new Collider[0];
        }

        /// <summary>Opens a shut door, shuts an open one.</summary>
        public void Toggle()
        {
            IsOpen = !IsOpen;
            _closeAt = Time.time + openSeconds;
        }

        private void Update()
        {
            if (pivot == null)
                return;

            if (IsOpen && Time.time >= _closeAt)
            {
                // Not while someone is standing in it. The leaf is solid again the moment
                // it stops, and a door that closed through a player would put a collider
                // inside them — the physics answer to which is to fire them out of it.
                if (Blocked())
                    _closeAt = Time.time + 1f;
                else
                    IsOpen = false;
            }

            float target = IsOpen ? openAngle : 0f;
            bool swinging = !Mathf.Approximately(_angle, target);
            if (swinging)
            {
                _angle = Mathf.MoveTowards(_angle, target, swingSpeed * Time.deltaTime);
                pivot.localRotation = Quaternion.Euler(0f, _angle, 0f);
            }
            if (swinging != IsSwinging)
            {
                IsSwinging = swinging;
                // The leaf is intangible while it travels. A moving solid would shove
                // whoever it caught — and a non-convex mesh collider being dragged through
                // the world every frame is the one thing Unity asks you not to do with one.
                // Nobody can cross in the half second it takes to swing anyway.
                SetLeafSolid(!swinging);
            }
        }

        private void SetLeafSolid(bool solid)
        {
            if (_leaf == null)
                return;
            for (int i = 0; i < _leaf.Length; ++i)
            {
                if (_leaf[i] != null)
                    _leaf[i].enabled = solid;
            }
        }

        /// <summary>Whether a character is standing in the doorway.</summary>
        private bool Blocked()
        {
            int found = Physics.OverlapBoxNonAlloc(
                transform.position + Vector3.up * Threshold.y,
                Threshold,
                _inTheWay,
                transform.rotation,
                ~0,
                QueryTriggerInteraction.Ignore);
            for (int i = 0; i < found; ++i)
            {
                if (_inTheWay[i] != null &&
                    _inTheWay[i].GetComponentInParent<BaseCharacterEntity>() != null)
                    return true;
            }
            return false;
        }
    }
}
