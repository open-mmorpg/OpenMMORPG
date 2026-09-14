using UnityEngine;

namespace MultiplayerARPG.Demo
{
    /// <summary>
    /// Shows a saved character on the select screen with the hair it was created with.
    ///
    /// The kit applies a saved character's body-part choices with SetupModelBodyParts,
    /// which looks for PlayerCharacterBodyPartComponent from the character *model*
    /// downward. The component itself can only live on the entity root: it reaches its
    /// entity with GetComponent on its own object, and SetModel writes the choice into
    /// that entity. On the kit's template prefabs the model sits on the root too, so the
    /// search starts where the component is. The demo keeps the model on a child (see
    /// DemoCharacterModelReveal), so the search starts one level below the component and
    /// finds nothing - nothing is logged, and every saved character simply comes up with
    /// the default hair. The create screen is unaffected, because UIBodyPartManager
    /// searches from the root instead; the two lookups disagree, which is worth raising
    /// upstream.
    ///
    /// The select screen raises eventOnSelectCharacter with the saved data just after it
    /// has shown the model, so this applies the data to the components on that model's
    /// entity and refreshes the model. Belongs on the same object as the UICharacterList
    /// (or UIMmoCharacterList) it serves; CanvasHome carries one on the select screen.
    /// </summary>
    [DisallowMultipleComponent]
    public class DemoSavedBodyParts : MonoBehaviour
    {
        private void Awake()
        {
            // Awake rather than OnEnable: the screen loads and selects its first character
            // from OnEnable, and only drops its listeners in OnDestroy.
            UICharacterList list = GetComponent<UICharacterList>();
            if (list == null)
            {
                Debug.LogWarning($"[{nameof(DemoSavedBodyParts)}] Nothing to listen to on \"{name}\". " +
                                 $"It belongs on the same object as a {nameof(UICharacterList)}.");
                enabled = false;
                return;
            }
            list.eventOnSelectCharacter.AddListener(data => Apply(list.SelectedModel, data as IPlayerCharacterData));
        }

        private static void Apply(BaseCharacterModel model, IPlayerCharacterData data)
        {
            if (model == null || data == null)
                return;
            // The entity this model belongs to, and only that one: every character on the
            // screen hangs under the same container, so a search from the container would
            // take in the others' components as well.
            BasePlayerCharacterEntity entity = model.GetComponentInParent<BasePlayerCharacterEntity>(true);
            if (entity == null)
                return;
            PlayerCharacterBodyPartComponent[] parts = entity.GetComponents<PlayerCharacterBodyPartComponent>();
            if (parts.Length == 0)
                return;
            foreach (PlayerCharacterBodyPartComponent part in parts)
            {
                if (!part.enabled)
                    continue;
                part.SetupCharacterModelEvents(model);
                part.ApplyModelAndColorBySavedData(data.PublicInts);
            }
            // The component asks the entity to refresh, but a screen's entity is not spawned
            // and never updates, so the refresh has to be asked for here - the same way the
            // screen dressed the model in the first place.
            model.SetEquipItemsImmediately(model.EquipItems, model.SelectableWeaponSets, model.EquipWeaponSet, model.IsWeaponsSheathed);
        }
    }
}
