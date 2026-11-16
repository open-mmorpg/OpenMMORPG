using UnityEngine;

namespace MultiplayerARPG
{
    /// <summary>
    /// Receives `OnAnimatorMove` from an `Animator` which is on another game object, see `AnimatorMoveRelay`.
    /// </summary>
    public interface IAnimatorMoveReceiver
    {
        /// <summary>
        /// Called from inside the animator's `OnAnimatorMove`, so `deltaPosition` and `deltaRotation` are valid.
        /// </summary>
        /// <param name="animator">The animator which is being evaluated</param>
        void OnChildAnimatorMove(Animator animator);
    }
}
