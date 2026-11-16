using UnityEngine;

namespace MultiplayerARPG
{
    /// <summary>
    /// Unity sends `OnAnimatorMove` only to components which are on the same game object as the `Animator`.
    /// An entity which keeps its model (and so its animator) on a child game object never receives it on its
    /// movement component, and its root motion was dropped without any warning.
    /// This component sits next to such an animator and forwards the message to the entity movement.
    /// Entity movement components add it by themselves, add it by `Attach` for a model which is created later.
    /// </summary>
    [DisallowMultipleComponent]
    public class AnimatorMoveRelay : MonoBehaviour
    {
        public Animator Animator { get; private set; }
        public IAnimatorMoveReceiver Receiver { get; private set; }

        private Object _receiverObject;

        /// <summary>
        /// Attach relays to animators of all models under the entity, and to the `fallbackAnimator`.
        /// Animators which are on the receiver's own game object are skipped, Unity sends them the message directly.
        /// </summary>
        public static void AttachToModels(GameObject entityRoot, Animator fallbackAnimator, IAnimatorMoveReceiver receiver)
        {
            if (entityRoot == null || receiver == null)
                return;
            IModelWithAnimator[] models = entityRoot.GetComponentsInChildren<IModelWithAnimator>(true);
            for (int i = 0; i < models.Length; ++i)
            {
                Attach(models[i].Animator, receiver);
            }
            Attach(fallbackAnimator, receiver);
        }

        /// <summary>
        /// Attach a relay to the animator, returns `null` if it is not necessary (the animator is on the receiver's game object).
        /// </summary>
        public static AnimatorMoveRelay Attach(Animator animator, IAnimatorMoveReceiver receiver)
        {
            if (animator == null || receiver == null)
                return null;
            Component receiverComponent = receiver as Component;
            if (receiverComponent != null && receiverComponent.gameObject == animator.gameObject)
                return null;
            AnimatorMoveRelay relay = animator.GetComponent<AnimatorMoveRelay>();
            if (relay == null)
                relay = animator.gameObject.AddComponent<AnimatorMoveRelay>();
            relay.Animator = animator;
            relay.Receiver = receiver;
            relay._receiverObject = receiver as Object;
            return relay;
        }

        private void OnAnimatorMove()
        {
            if (Receiver == null || Animator == null)
                return;
            // The receiver is a destroyed component
            if (!ReferenceEquals(_receiverObject, null) && _receiverObject == null)
                return;
            Receiver.OnChildAnimatorMove(Animator);
        }
    }
}
