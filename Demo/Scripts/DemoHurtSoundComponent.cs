using Insthync.AudioManager;
using UnityEngine;

namespace MultiplayerARPG.Demo
{
    /// <summary>
    /// A grunt when the character takes damage.
    ///
    /// The kit has a footstep component and a death component but nothing for being hit:
    /// its damage event fires on the server, where there is no audio, and the hurt
    /// animation is a plain state with no clip slot. What every client does see is the
    /// synced HP, so this plays a clip whenever it drops, throttled so a burst of hits is
    /// one grunt. Runs on clients only. Clips are assigned by the demo's audio wiring
    /// (ManHit* for the men, WomanHit* for the women).
    /// </summary>
    public class DemoHurtSoundComponent : MonoBehaviour
    {
        public AudioClip[] clips = new AudioClip[0];
        [Tooltip("Shortest gap between two grunts, in seconds.")]
        public float cooldown = 0.35f;
        public float minPitch = 0.95f;
        public float maxPitch = 1.05f;
        [Range(0f, 1f)]
        public float volume = 1f;

        private DamageableEntity _entity;
        private AudioSource _source;
        private float _readyAt;

        private void Start()
        {
            _entity = GetComponent<DamageableEntity>();
            if (_entity == null || !_entity.IsClient)
            {
                enabled = false;
                return;
            }
            _entity.onCurrentHpChange += OnHpChange;
        }

        private void OnDestroy()
        {
            if (_entity != null)
                _entity.onCurrentHpChange -= OnHpChange;
        }

        private void OnHpChange(DamageableEntity entity, int oldHp, int newHp)
        {
            if (newHp >= oldHp || clips.Length == 0 || Time.time < _readyAt)
                return;
            if (Application.isBatchMode || AudioListener.pause)
                return;
            if (_source == null)
            {
                var go = new GameObject("_HurtAudioSource");
                go.transform.SetParent(transform, false);
                go.transform.localPosition = new Vector3(0f, 1.5f, 0f);
                _source = go.AddComponent<AudioSource>();
                _source.spatialBlend = 1f;
            }
            _readyAt = Time.time + cooldown;
            _source.pitch = Random.Range(minPitch, maxPitch);
            float level = AudioManager.Singleton.GetVolumeLevel(AudioManager.Singleton.sfxVolumeSetting.id);
            _source.PlayOneShot(clips[Random.Range(0, clips.Length)], volume * level);
        }
    }
}
