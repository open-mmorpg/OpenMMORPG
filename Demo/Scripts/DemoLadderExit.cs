using UnityEngine;

namespace MultiplayerARPG.Demo
{
    /// <summary>
    /// Lets the local player off a ladder at either end at any frame rate.
    ///
    /// The kit decides you have reached an end when a frame's climb carries you more than
    /// five centimetres past it. Each frame it pulls you back onto the ladder's line and
    /// then moves you your speed times the frame's length along it, so how far past the
    /// end you get IS speed times frame length: seven centimetres at 60 frames a second
    /// for this demo's four metres a second, and three at 144. Above about 80 frames a
    /// second the character reaches the top and hangs there, climbing on the spot.
    ///
    /// This asks the same question with the frame length taken out of it: at an end and
    /// still pushing past it means leaving. It asks it only for the character the local
    /// player is driving, because leaving is a command sent to the server, and the kit's
    /// own check is made on that client as well; both set the same flag first, so
    /// whichever notices first has it and the other stands down.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Ladder))]
    public class DemoLadderExit : MonoBehaviour
    {
        /// <summary>How near an end counts as at it, in metres.</summary>
        private const float Tolerance = 0.02f;

        private Ladder _ladder;

        private void Awake()
        {
            _ladder = GetComponent<Ladder>();
        }

        private void Update()
        {
            BasePlayerCharacterController controller = BasePlayerCharacterController.Singleton;
            if (controller == null)
                return;
            BasePlayerCharacterEntity player = controller.PlayingCharacterEntity;
            if (player == null)
                return;
            CharacterLadderComponent climb = player.LadderComponent;
            if (climb == null || climb.ClimbingLadder != _ladder || climb.EnterExitState != EnterExitState.None)
                return;

            Vector3 bottom = _ladder.bottomTransform.position;
            float length = Vector3.Distance(bottom, _ladder.topTransform.position);
            float along = Vector3.Dot(player.EntityTransform.position - bottom, _ladder.Up);
            MovementState state = player.MovementState;
            if (state.Has(MovementState.Up) && along >= length - Tolerance)
                Leave(climb, LadderEntranceType.Top);
            else if (state.Has(MovementState.Down) && along <= Tolerance)
                Leave(climb, LadderEntranceType.Bottom);
        }

        private static void Leave(CharacterLadderComponent climb, LadderEntranceType end)
        {
            // The same two steps the kit takes: flag the request so nothing else is sent
            // while it is in flight, then ask the server.
            climb.EnterExitState = EnterExitState.ConfirmAwaiting;
            climb.CallCmdExitLadder(end);
        }
    }
}
