using UnityEngine;

namespace MultiplayerARPG.Demo
{
    /// <summary>
    /// Hangs an equipment item on a character in the editor the way the kit hangs it at
    /// runtime — same socket, same offsets, read from the item's own game data — so a grip
    /// can be judged and nudged without entering play mode or equipping anything.
    ///
    /// Point <see cref="item"/> at a weapon or shield and its model appears on the matching
    /// socket. Tick <see cref="overrideOffsets"/> to nudge position, rotation and scale live;
    /// the numbers you settle on are the ones to put into `DemoItemBuilder`, which is what
    /// writes `equipmentModels` for real — hand-edits to the item asset are wiped by the next
    /// `Build Items`. <see cref="LogOffsets"/> prints them ready to paste.
    ///
    /// The spawned model is marked `DontSave`, so previewing does not dirty the scene or
    /// leave stray objects behind in a build.
    ///
    /// This only mirrors the *rigid prop* path — a socket, a mesh and a transform, which is
    /// all weapons and shields use. Armour is skinned and gets its bones rebound instead, so
    /// it is not previewed here; the demo's NPC models have their outfits grafted in already
    /// (see `DemoCharacterBuilder`).
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    public class DemoEquipPreview : MonoBehaviour
    {
        [Tooltip("Weapon or shield to show. Its own equipSocket and offsets are used, exactly as at runtime.")]
        public BaseEquipmentItem item;

        [Tooltip("Use the offsets below instead of the item's stored ones, so they can be nudged live.")]
        public bool overrideOffsets;
        public Vector3 localPosition = Vector3.zero;
        public Vector3 localEulerAngles = Vector3.zero;
        public Vector3 localScale = Vector3.one;

        [Tooltip("Which entry of the item's equipmentModels to show. Almost always 0.")]
        public int modelIndex;

        [Tooltip("Whether the model is on. Equipping re-reads the item's saved game data, " +
                 "so unequip/equip is the quick way to see whether a grip actually saved.")]
        public bool equipped = true;

        private GameObject _spawned;
        private bool _shownEquipped;

        /// <summary>The live preview object, or null when nothing is equipped.</summary>
        public Transform Spawned
        {
            get { return _spawned != null ? _spawned.transform : null; }
        }
        private BaseEquipmentItem _shownItem;
        private int _shownIndex = -1;
        private bool _shownOverride;
        private Vector3 _shownPosition, _shownEuler, _shownScale;

        /// <summary>Set by OnValidate, which cannot rebuild the preview itself.</summary>
        private bool _revalidate;

        private void OnEnable()
        {
            Refresh();
        }

        private void OnDisable()
        {
            Clear();
        }

        private void OnValidate()
        {
            // OnValidate can fire during serialisation, where destroying is illegal, so the
            // rebuild waits for the next editor tick instead.
            _revalidate = true;
        }

        private void Update()
        {
            // a drag in the scene view is an edit too, and must be taken before the
            // field comparison below decides nothing has changed
            if (CaptureDrag())
                return;

            // A tuned grip belongs to the item it was tuned against. Switching item has to
            // drop the override, or the previous weapon's offsets are pasted onto the new
            // one — and then saved over its real ones. That is how BanditAxe and
            // ApprenticeStaff once ended up with byte-identical grips, and why a weapon
            // looked like it "forgot" a saved offset: the stale override hid it.
            if (item != _shownItem || modelIndex != _shownIndex)
            {
                overrideOffsets = false;
                _revalidate = false;
                Refresh();
                return;
            }

            if (!_revalidate &&
                equipped == _shownEquipped &&
                overrideOffsets == _shownOverride &&
                localPosition == _shownPosition &&
                localEulerAngles == _shownEuler &&
                localScale == _shownScale)
                return;
            _revalidate = false;
            Refresh();
        }

        /// <summary>
        /// Notices the preview being dragged in the scene view and copies its transform into
        /// this component's serialised fields.
        ///
        /// Without this a drag is lost the moment anything rebuilds the preview — a script
        /// recompile is enough — because the spawned object is <see cref="HideFlags.DontSave"/>
        /// and never reaches the scene file. Capturing it here makes the adjustment stick, and
        /// gives "the grip currently in the scene" one unambiguous source.
        /// </summary>
        private bool CaptureDrag()
        {
            if (_spawned == null)
                return false;

            Transform t = _spawned.transform;
            // rotation is compared as a rotation, not as euler numbers: Unity re-expresses
            // localEulerAngles freely (80 becomes 79.99999, 0 becomes 360) and comparing the
            // components would fire this every frame
            bool moved = (t.localPosition - _shownPosition).sqrMagnitude > 1e-10f;
            bool scaled = (t.localScale - _shownScale).sqrMagnitude > 1e-10f;
            bool turned = Quaternion.Angle(t.localRotation, Quaternion.Euler(_shownEuler)) > 0.01f;
            if (!moved && !scaled && !turned)
                return false;

            localPosition = t.localPosition;
            localEulerAngles = t.localEulerAngles;
            localScale = t.localScale;
            overrideOffsets = true;

            _shownPosition = localPosition;
            _shownEuler = localEulerAngles;
            _shownScale = localScale;
            _shownOverride = true;
#if UNITY_EDITOR
            if (!Application.isPlaying)
                UnityEditor.EditorUtility.SetDirty(this);
#endif
            return true;
        }

        /// <summary>Rebuilds the preview. Safe to call at any time.</summary>
        public void Refresh()
        {
            Clear();

            _shownItem = item;
            _shownIndex = modelIndex;
            _shownEquipped = equipped;
            _shownOverride = overrideOffsets;
            _shownPosition = localPosition;
            _shownEuler = localEulerAngles;
            _shownScale = localScale;

            if (!equipped)
                return;

            if (item == null || item.EquipmentModels == null ||
                modelIndex < 0 || modelIndex >= item.EquipmentModels.Length)
                return;

            EquipmentModel model = item.EquipmentModels[modelIndex];
            if (model == null || model.MeshPrefab == null)
                return;

            Transform socket = FindSocket(model.equipSocket);
            if (socket == null)
            {
                Debug.LogWarning($"[{nameof(DemoEquipPreview)}] No equipment container named " +
                                 $"\"{model.equipSocket}\" on \"{name}\", so \"{item.name}\" cannot be shown. " +
                                 "Rebuild the character models if the sockets have changed.", this);
                return;
            }

            if (!overrideOffsets)
            {
                // show what the item actually stores, so the fields start from the real values
                localPosition = model.localPosition;
                localEulerAngles = model.localEulerAngles;
                localScale = model.doNotChangeScale ? Vector3.one : model.localScale;
                _shownPosition = localPosition;
                _shownEuler = localEulerAngles;
                _shownScale = localScale;
            }

            _spawned = Instantiate(model.MeshPrefab, socket);
            _spawned.name = $"~Preview_{item.name}";
            _spawned.hideFlags = HideFlags.DontSave;
            _spawned.transform.localPosition = localPosition;
            _spawned.transform.localEulerAngles = localEulerAngles;
            _spawned.transform.localScale = localScale;
        }

        private void Clear()
        {
            if (_spawned == null)
                return;
            if (Application.isPlaying)
                Destroy(_spawned);
            else
                DestroyImmediate(_spawned);
            _spawned = null;
        }

        /// <summary>
        /// Finds the socket by name rather than by reading `equipmentContainers`, which is
        /// protected on `BaseCharacterModel` and has no public accessor. That is safe here
        /// because `DemoCharacterBuilder.CreateSocket` names each socket GameObject after the
        /// very `equipSocket` string the item asks for, so the two cannot drift apart — but it
        /// does mean this only works on characters that builder produced.
        /// </summary>
        private Transform FindSocket(string equipSocket)
        {
            if (string.IsNullOrEmpty(equipSocket))
                return null;
            if (GetComponentInChildren<BaseCharacterModel>(true) == null)
                return null;
            foreach (Transform candidate in GetComponentsInChildren<Transform>(true))
            {
                if (candidate.name == equipSocket)
                    return candidate;
            }
            return null;
        }

        /// <summary>
        /// Puts the model on, reading the item's **saved** game data rather than any tuning
        /// still held on this component. Unequip followed by Equip is therefore a direct
        /// answer to "did that grip actually save?" — whatever comes back is what the item
        /// stores.
        ///
        /// Unsaved offsets are dropped, which is the point: they are exactly what would be
        /// masking the stored values.
        /// </summary>
        [ContextMenu("Equip (reload from saved game data)")]
        public void Equip()
        {
            equipped = true;
            overrideOffsets = false;
            Refresh();
        }

        /// <summary>Takes the model off. The offsets on this component are left untouched.</summary>
        [ContextMenu("Unequip")]
        public void Unequip()
        {
            equipped = false;
            Refresh();
        }

#if UNITY_EDITOR
        /// <summary>
        /// Writes the grip currently shown in the scene into the weapon's game data.
        ///
        /// The values are taken from the live preview object, so whatever was dragged in the
        /// scene view is what gets saved. They go two places: the item asset, so the change
        /// is live immediately, and <see cref="DemoWeaponGripOverrides"/>, so it survives the
        /// next `Build Items` — which rewrites every item's `equipmentModels` from scratch and
        /// would otherwise wipe it.
        ///
        /// Saving replaces the builder's default orientation for that item outright, so the
        /// captured transform must be the whole grip, not a delta.
        /// </summary>
        [ContextMenu("Save grip to weapon game data")]
        public void SaveGripToGameData()
        {
            if (item == null)
            {
                Debug.LogWarning($"[{nameof(DemoEquipPreview)}] No item on \"{name}\", nothing to save.", this);
                return;
            }
            if (item.EquipmentModels == null || modelIndex < 0 || modelIndex >= item.EquipmentModels.Length)
            {
                Debug.LogWarning($"[{nameof(DemoEquipPreview)}] \"{item.name}\" has no equipment model " +
                                 $"at index {modelIndex}, nothing to save.", this);
                return;
            }

            // the live object is the truth - it carries any scene-view drag
            Vector3 position = localPosition;
            Vector3 euler = localEulerAngles;
            Vector3 scale = localScale;
            if (_spawned != null)
            {
                position = _spawned.transform.localPosition;
                euler = _spawned.transform.localEulerAngles;
                scale = _spawned.transform.localScale;
            }

            DemoWeaponGripOverrides overrides = LoadOrCreateOverrides();
            overrides.Set(item.name, position, euler, scale);
            UnityEditor.EditorUtility.SetDirty(overrides);

            // apply to the item too, so the grip is right without waiting for a rebuild
            var serialized = new UnityEditor.SerializedObject(item);
            string root = $"equipmentModels.Array.data[{modelIndex}]";
            serialized.FindProperty(root + ".localPosition").vector3Value = position;
            serialized.FindProperty(root + ".localEulerAngles").vector3Value = euler;
            serialized.FindProperty(root + ".localScale").vector3Value = scale;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            UnityEditor.EditorUtility.SetDirty(item);
            UnityEditor.AssetDatabase.SaveAssets();

            Debug.Log($"[{nameof(DemoEquipPreview)}] Saved grip for \"{item.name}\"\n" +
                      $"    localPosition    = {position.ToString("F4")}\n" +
                      $"    localEulerAngles = {euler.ToString("F3")}\n" +
                      $"    localScale       = {scale.ToString("F4")}\n" +
                      $"    -> {item.name}.asset and {DemoWeaponGripOverrides.AssetPath}", item);
        }

        private static DemoWeaponGripOverrides LoadOrCreateOverrides()
        {
            var existing = UnityEditor.AssetDatabase.LoadAssetAtPath<DemoWeaponGripOverrides>(
                DemoWeaponGripOverrides.AssetPath);
            if (existing != null)
                return existing;
            var created = ScriptableObject.CreateInstance<DemoWeaponGripOverrides>();
            UnityEditor.AssetDatabase.CreateAsset(created, DemoWeaponGripOverrides.AssetPath);
            Debug.Log($"[{nameof(DemoEquipPreview)}] Created {DemoWeaponGripOverrides.AssetPath}.");
            return created;
        }
#endif

        /// <summary>
        /// Prints the current offsets in the shape `DemoItemBuilder` wants, so a value tuned
        /// here can be moved somewhere it survives a rebuild.
        /// </summary>
        [ContextMenu("Log offsets for DemoItemBuilder")]
        public void LogOffsets()
        {
            Debug.Log($"[{nameof(DemoEquipPreview)}] {(item != null ? item.name : "(no item)")}\n" +
                      $"    localPosition   = new Vector3({localPosition.x:0.####}f, {localPosition.y:0.####}f, {localPosition.z:0.####}f)\n" +
                      $"    localEulerAngles= new Vector3({localEulerAngles.x:0.####}f, {localEulerAngles.y:0.####}f, {localEulerAngles.z:0.####}f)\n" +
                      $"    localScale      = new Vector3({localScale.x:0.####}f, {localScale.y:0.####}f, {localScale.z:0.####}f)", this);
        }
    }
}
