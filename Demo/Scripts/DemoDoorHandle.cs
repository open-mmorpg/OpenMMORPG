using UnityEngine;

namespace MultiplayerARPG.Demo
{
    /// <summary>
    /// Makes a door leaf something a player can look at and open, and lights it up while
    /// they are looking at it.
    ///
    /// It sits on the leaf rather than on the doorway because of how the kit finds what you
    /// are pointing at: the controller casts a ray from the middle of the screen and asks
    /// each collider it passes for an <see cref="IBaseActivatableEntity"/> — with
    /// <c>GetComponent</c>, on the collider's own object, so a component anywhere else in
    /// the hierarchy is not found. The leaf carries the collider, so the leaf carries this.
    ///
    /// Nothing here is networked. A door is a piece of scenery that remembers one bit, and
    /// <see cref="DemoDoor"/> says why that bit is not worth sending.
    /// </summary>
    [DisallowMultipleComponent]
    public class DemoDoorHandle : MonoBehaviour, IActivatableEntity
    {
        [Tooltip("The door this leaf belongs to.")]
        public DemoDoor door;

        /// <summary>
        /// How close a player has to be, in metres. Matched to the distance the kit lets you
        /// talk to someone from, because reaching a door handle and reaching a shopkeeper
        /// are the same gesture as far as a player is concerned.
        /// </summary>
        [Tooltip("How close a player has to stand to open it, in metres.")]
        public float activateDistance = 3f;

        /// <summary>
        /// What the leaf is multiplied by while it is the thing a player is pointing at.
        ///
        /// Brightening rather than outlining it, because the kit gives a door no interface
        /// of its own: it is not a game entity, so none of the selected-target UI appears
        /// for one. Warm rather than white so that it reads as lamplight on wood instead of
        /// as something switching on.
        /// </summary>
        [Tooltip("Tint applied to the leaf while a player is looking at it.")]
        public Color highlight = new Color(1.5f, 1.38f, 1.1f, 1f);

        private Renderer _leaf;
        private MaterialPropertyBlock _block;
        private bool _lit;

        private static readonly int BaseColor = Shader.PropertyToID("_BaseColor");

        public Transform EntityTransform { get { return transform; } }

        public GameObject EntityGameObject { get { return gameObject; } }

        private void Awake()
        {
            _leaf = GetComponent<Renderer>();
            if (_leaf == null)
                _leaf = GetComponentInChildren<Renderer>();
            _block = new MaterialPropertyBlock();
        }

        private void Update()
        {
            // The controller clears and re-finds its target every frame, and only accepts
            // one inside GetActivatableDistance - so "is this the target" already means
            // "is this close enough to open", which is exactly what the light should say.
            BasePlayerCharacterController controller = BasePlayerCharacterController.Singleton;
            bool lit = controller != null && ReferenceEquals(controller.SelectedEntity, this);
            if (lit == _lit)
                return;
            _lit = lit;
            Light(lit);
        }

        private void OnDisable()
        {
            if (_lit)
            {
                _lit = false;
                Light(false);
            }
        }

        /// <summary>
        /// Tints the leaf through a property block, so the one material every door in the
        /// village shares is not touched — set the tint on that and all six light up at once.
        /// </summary>
        private void Light(bool on)
        {
            if (_leaf == null)
                return;
            _leaf.GetPropertyBlock(_block);
            _block.SetColor(BaseColor, on ? highlight : Color.white);
            _leaf.SetPropertyBlock(_block);
        }

        public float GetActivatableDistance()
        {
            return activateDistance;
        }

        /// <summary>One press, one swing — the door is not a conversation to stay inside of.</summary>
        public bool ShouldClearTargetAfterActivated()
        {
            return true;
        }

        public bool SetAsTargetInOneClick()
        {
            return true;
        }

        public bool NotBeingSelectedOnClick()
        {
            return false;
        }

        /// <summary>A door is scenery, not an enemy: aiming at one must not start a fight.</summary>
        public bool ShouldBeAttackTarget()
        {
            return false;
        }

        /// <summary>
        /// Walking up to a door does not open it — that is the whole point of it being a
        /// door — so it opens on the key and on nothing else.
        /// </summary>
        public bool ShouldNotActivateAfterFollowed()
        {
            return true;
        }

        /// <summary>Not while it is already moving, or a press mid-swing reverses it.</summary>
        public bool CanActivate()
        {
            return door != null && !door.IsSwinging;
        }

        public void OnActivate()
        {
            door.Toggle();
        }
    }
}
