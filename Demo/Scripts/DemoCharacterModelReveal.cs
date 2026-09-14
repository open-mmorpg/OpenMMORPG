using UnityEngine;

namespace MultiplayerARPG.Demo
{
    /// <summary>
    /// Lets the character screens show an entity whose model hangs off a child.
    ///
    /// The create and select screens instantiate a whole character entity under their
    /// model container, then address it only through its CharacterModel. They hide
    /// everything with characterModelContainer.SetChildrenActive(false), which switches
    /// off the entity, and show the chosen one with SelectedModel.gameObject.SetActive
    /// (true), which switches on the model. On the kit's template prefabs the model
    /// component sits on the entity's own root, so those are the same object and it
    /// works. The demo entities keep the model on a child - DemoEntityBuilder does that
    /// deliberately, so a character can change model without the entity being rebuilt -
    /// and the model is then switched on underneath a parent that was just switched off.
    /// It ends up active, in front of the camera, and invisible, with nothing logged and
    /// no exception thrown.
    ///
    /// Both screens raise eventOnShowInstantiatedCharacter immediately after that
    /// activation, which is where this comes in: the branch is switched back on from
    /// here, so nothing in the kit itself has to change.
    ///
    /// Belongs on the same object as the screen it serves. CanvasHome carries one on
    /// each of the character create and character select screens.
    /// </summary>
    [DisallowMultipleComponent]
    public class DemoCharacterModelReveal : MonoBehaviour
    {
        private void Awake()
        {
            // Awake rather than OnEnable, because the screens load their characters from
            // OnEnable and select the first one as part of that load. The screens drop
            // their listeners in OnDestroy only, so subscribing once here is enough.
            bool found = false;

            UICharacterCreate create = GetComponent<UICharacterCreate>();
            if (create != null)
            {
                create.eventOnShowInstantiatedCharacter.AddListener(
                    model => Reveal(model, create.characterModelContainer));
                found = true;
            }

            UICharacterList list = GetComponent<UICharacterList>();
            if (list != null)
            {
                list.eventOnShowInstantiatedCharacter.AddListener(
                    model => Reveal(model, list.characterModelContainer));
                found = true;
            }

            if (!found)
            {
                Debug.LogWarning($"[{nameof(DemoCharacterModelReveal)}] Nothing to listen to on \"{name}\". " +
                                 $"It belongs on the same object as a {nameof(UICharacterCreate)} or a " +
                                 $"{nameof(UICharacterList)}.");
                enabled = false;
            }
        }

        /// <summary>
        /// Switches on everything between the container and the model.
        /// </summary>
        private static void Reveal(BaseCharacterModel model, Transform container)
        {
            if (model == null || container == null)
                return;
            // Checked before anything is switched on, because the walk stops at the
            // container: a model from somewhere else would send it up into the rest of
            // the canvas, putting a screen on display that was deliberately hidden.
            if (!model.transform.IsChildOf(container))
                return;
            Transform branch = model.transform;
            while (branch != null && branch != container)
            {
                branch.gameObject.SetActive(true);
                branch = branch.parent;
            }
        }
    }
}
