using UnityEngine;

namespace MultiplayerARPG.Demo
{
    /// <summary>
    /// Walks an NPC round a loop of points, standing a while at each.
    ///
    /// The kit's NPCs stand still; its walking things are monsters, with a whole AI
    /// behind them. A guard on his rounds needs none of that: the server hands the
    /// entity's navmesh mover one point at a time and waits for it to get there, and
    /// the kit does the rest - the mover paths round whatever is in the way, turns him to
    /// face where he is going, syncs him to the clients, and the base entity plays the
    /// walk from the movement state. An NPC has no move speed of its own, so his comes
    /// from the entity's override, which the kit syncs too.
    ///
    /// He holds still while a player is close enough to talk, so that a conversation is
    /// not walked out of, and carries on when they step away.
    ///
    /// The one thing the kit does not give an NPC is permission: a base entity answers
    /// no to "can it move" and "can it turn", and the navmesh mover drops every
    /// destination on that answer. It asks through a validation hook, though, which is
    /// where this says yes.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(NpcEntity))]
    public class DemoPatrol : MonoBehaviour
    {
        [Tooltip("World positions, walked in order and then round again.")]
        public Vector3[] waypoints = new Vector3[0];

        [Tooltip("Walking pace, in metres a second. A stroll, not a march.")]
        public float walkSpeed = 1.4f;

        [Tooltip("How long to stand at each point, in seconds.")]
        public float pause = 5f;

        [Tooltip("How near a point counts as reaching it, in metres.")]
        public float arriveDistance = 0.7f;

        [Tooltip("A player within this many metres holds the patrol until they leave.")]
        public float holdForPlayersWithin = 3.5f;

        [Tooltip("Give up on a point after this many seconds and go on to the next.")]
        public float legTimeout = 30f;

        private NpcEntity _entity;
        private int _next;
        private float _waitUntil;
        private float _legStarted;
        private bool _walking;
        private bool _held;
        private float _nextPlayerCheck;

        private void Awake()
        {
            _entity = GetComponent<NpcEntity>();
        }

        // An NPC answers no when the kit asks whether it may move or turn - its movers
        // refuse every destination on that answer - and the kit asks through these
        // hooks so that something else can answer for it. This guard may.
        private void OnEnable()
        {
            _entity.onCanMoveValidated += Allow;
            _entity.onCanTurnValidated += Allow;
        }

        private void OnDisable()
        {
            _entity.onCanMoveValidated -= Allow;
            _entity.onCanTurnValidated -= Allow;
        }

        private static void Allow(BaseGameEntity entity, ref bool allowed)
        {
            allowed = true;
        }

        private void Update()
        {
            if (!_entity.IsServer || waypoints == null || waypoints.Length == 0)
                return;
            if (_entity.OverrideMoveSpeed != walkSpeed)
                _entity.OverrideMoveSpeed = walkSpeed;

            if (Time.time >= _nextPlayerCheck)
            {
                _nextPlayerCheck = Time.time + 0.5f;
                bool near = PlayerNearby();
                if (near && _walking)
                {
                    _entity.StopMove();
                    _walking = false;
                }
                if (near)
                    _waitUntil = Time.time + pause;
                _held = near;
            }
            if (_held)
                return;

            if (_walking)
            {
                Vector3 here = _entity.EntityTransform.position;
                Vector3 there = waypoints[_next];
                here.y = 0f;
                there.y = 0f;
                if (Vector3.Distance(here, there) <= arriveDistance || Time.time - _legStarted > legTimeout)
                {
                    _entity.StopMove();
                    _walking = false;
                    _next = (_next + 1) % waypoints.Length;
                    _waitUntil = Time.time + pause;
                }
                return;
            }

            if (Time.time >= _waitUntil)
            {
                _entity.PointClickMovement(waypoints[_next]);
                _legStarted = Time.time;
                _walking = true;
            }
        }

        private bool PlayerNearby()
        {
            Collider[] hits = Physics.OverlapSphere(_entity.EntityTransform.position, holdForPlayersWithin);
            foreach (Collider hit in hits)
            {
                if (hit.transform.root.GetComponent<BasePlayerCharacterEntity>() != null)
                    return true;
            }
            return false;
        }
    }
}
