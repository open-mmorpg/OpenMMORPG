using Insthync.AudioManager;
using UnityEngine;

namespace MultiplayerARPG.Demo
{
    /// <summary>
    /// A looping ambience bed that follows the ambient volume setting, and optionally
    /// fades with height so the sea is loud on the beach and a murmur up on the hill.
    ///
    /// The kit's AudioSourceSetter applies the setting once when it starts to play; this
    /// keeps applying it, so the slider works while the loop runs, and can shape the
    /// volume by where the listener is. The listener is the owning character's, which
    /// only exists once the player is in the world, so it is looked up again until found.
    /// Built into the island scene by DemoSceneBuilder.
    /// </summary>
    [RequireComponent(typeof(AudioSource))]
    public class DemoAmbientLoop : MonoBehaviour
    {
        [Range(0f, 1f)]
        public float baseVolume = 1f;
        [Tooltip("Fade the loop out as the listener climbs above the sea.")]
        public bool fadeWithHeight;
        public float seaLevel = 0f;
        [Tooltip("Up to this height above the sea the loop is at full volume.")]
        public float fullBelowHeight = 1.5f;
        [Tooltip("At this height above the sea the loop is down to its quiet volume.")]
        public float quietAboveHeight = 14f;
        [Range(0f, 1f)]
        public float quietVolume = 0.1f;

        private AudioSource _source;
        private AudioListener _listener;
        private float _nextSearch;

        private void Awake()
        {
            _source = GetComponent<AudioSource>();
            _source.loop = true;
            _source.spatialBlend = 0f;
        }

        private void Update()
        {
            float factor = 1f;
            if (fadeWithHeight)
            {
                if (_listener == null || !_listener.isActiveAndEnabled)
                {
                    if (Time.time >= _nextSearch)
                    {
                        _nextSearch = Time.time + 1f;
                        _listener = FindListener();
                    }
                }
                if (_listener != null)
                {
                    float height = _listener.transform.position.y - seaLevel;
                    factor = Mathf.Lerp(1f, quietVolume, Mathf.InverseLerp(fullBelowHeight, quietAboveHeight, height));
                }
            }
            AudioManager manager = AudioManager.Singleton;
            float level = manager == null ? 1f : manager.GetVolumeLevel(manager.ambientVolumeSetting.id);
            _source.volume = baseVolume * factor * level;
            _source.mute = level <= 0f;
        }

        private static AudioListener FindListener()
        {
            foreach (AudioListener listener in FindObjectsByType<AudioListener>(FindObjectsSortMode.None))
            {
                if (listener.isActiveAndEnabled)
                    return listener;
            }
            return null;
        }
    }
}
