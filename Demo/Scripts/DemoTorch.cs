using UnityEngine;

namespace MultiplayerARPG.Demo
{
    /// <summary>
    /// A fire that keeps the village's hours: lit as the dusk comes on, out again after
    /// dawn, and burning in between.
    ///
    /// It reads the hour off <see cref="DemoSkyCycle"/>, which is already the one thing
    /// in the scene that knows what time it is, so a torch and the sky can never
    /// disagree about whether it is night. Nothing here is sent over the network: the
    /// clock is the server's, and every client lights its torches off the same hour.
    ///
    /// Nothing switches. The fire has a strength between out and full, and both the
    /// light and the flame follow it: the strength climbs with the dusk over a window of
    /// the night amount, and is also rate-limited in real seconds, so a torch never
    /// pops on in a single frame even when the clock jumps. Each torch also lights a
    /// little before or after its neighbours, taken from where it stands, because a
    /// dozen torches all catching in the same instant reads as a switch being thrown.
    /// </summary>
    [ExecuteAlways]
    public class DemoTorch : MonoBehaviour
    {
        public enum Schedule
        {
            /// <summary>Lit through the night, out by day: a street torch.</summary>
            Night,
            /// <summary>Lit through the day, out at night: daylight through a window.</summary>
            Day,
            /// <summary>Never out: a campfire someone keeps fed.</summary>
            Always,
        }

        [Header("What burns")]
        [Tooltip("The light the fire throws. Its intensity is driven from here, so set the strength below rather than on the light.")]
        public Light lamp;
        [Tooltip("The flame, embers and smoke. Their emission is scaled with the fire's strength, so they build up and die down rather than start and stop.")]
        public ParticleSystem[] flames;

        [Header("When it burns")]
        public Schedule schedule = Schedule.Night;
        [Tooltip("How far into night the fire is half lit. 0 is full day, 1 full night, and 0.5 is the moment the sun touches the horizon.")]
        [Range(0f, 1f)] public float ignitesAt = 0.55f;
        [Tooltip("How much of the night amount the fade spreads across, either side of ignitesAt.")]
        [Range(0.02f, 0.5f)] public float ignitionSpread = 0.15f;
        [Tooltip("How far each torch's ignition drifts from ignitesAt, so neighbours do not all catch at once.")]
        [Range(0f, 0.3f)] public float stagger = 0.08f;
        [Tooltip("The least time, in real seconds, the fire takes to come up from nothing or die down to it.")]
        public float fadeSeconds = 8f;

        [Header("How it burns")]
        [Tooltip("The light's intensity when the fire is fully lit.")]
        public float intensity = 1.6f;
        [Tooltip("How much the light wavers, as a fraction of its intensity.")]
        [Range(0f, 1f)] public float flicker = 0.18f;
        public float flickerSpeed = 11f;
        [Tooltip("How far the light wanders, in metres, as the flame leans about.")]
        public float sway = 0.03f;

        /// <summary>How lit the fire is now, from 0 (out) to 1 (full).</summary>
        public float Lit { get { return _lit; } }

        private float _lit = -1f;
        private float _seed;
        private Vector3 _lampHome;
        private float[] _rates;
        private static DemoSkyCycle _sky;

        private void OnEnable()
        {
            // Taken from where the torch stands so it is the same every time the scene
            // loads, and different from the torch next door.
            Vector3 p = transform.position;
            _seed = Mathf.Repeat(Mathf.Abs(p.x * 12.9898f + p.y * 78.233f + p.z * 37.719f), 1000f);
            if (lamp != null)
                _lampHome = lamp.transform.localPosition;
            if (flames != null)
            {
                _rates = new float[flames.Length];
                for (int i = 0; i < flames.Length; ++i)
                    _rates[i] = flames[i] == null ? 0f : flames[i].emission.rateOverTimeMultiplier;
            }
            _lit = -1f;
        }

        /// <summary>
        /// Puts the fire straight into the state the hour calls for, with no fade. For
        /// the builder, so a scene is saved with its torches out at the noon it is
        /// previewed at rather than however the prefab happened to be left.
        /// </summary>
        public void Settle()
        {
            _lit = Target();
            Burn(0f);
        }

        private void Update()
        {
            float target = Target();
            if (Application.isPlaying && _lit >= 0f)
                _lit = Mathf.MoveTowards(_lit, target, Time.deltaTime / Mathf.Max(0.1f, fadeSeconds));
            else
                _lit = target;

            Burn(Application.isPlaying ? Time.time : 0f);
        }

        /// <summary>Where the fire is heading: how lit the hour says it should be.</summary>
        private float Target()
        {
            if (schedule == Schedule.Always)
                return 1f;
            if (_sky == null)
                _sky = FindAnyObjectByType<DemoSkyCycle>();
            if (_sky == null)
                return schedule == Schedule.Night ? 1f : 0f;

            float night = _sky.NightAmount(_sky.Hour);
            float at = ignitesAt + (Mathf.Repeat(_seed * 0.618f, 1f) - 0.5f) * 2f * stagger;
            float lit = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(at - ignitionSpread, at + ignitionSpread, night));
            return schedule == Schedule.Night ? lit : 1f - lit;
        }

        private void Burn(float time)
        {
            if (lamp != null)
            {
                float waver = 1f;
                Vector3 lean = Vector3.zero;
                if (time > 0f && flicker > 0f)
                {
                    // Two noises, one quick and one slow, so it neither strobes nor
                    // breathes. Both are Perlin, which never jumps.
                    float quick = Mathf.PerlinNoise(time * flickerSpeed, _seed);
                    float slow = Mathf.PerlinNoise(time * flickerSpeed * 0.23f, _seed + 17f);
                    waver = 1f + flicker * ((quick * 0.65f + slow * 0.35f) * 2f - 1f);
                    lean = new Vector3(
                        Mathf.PerlinNoise(time * 2.3f, _seed + 5f) - 0.5f,
                        Mathf.PerlinNoise(time * 3.1f, _seed + 9f) - 0.5f,
                        Mathf.PerlinNoise(time * 2.7f, _seed + 13f) - 0.5f) * (2f * sway);
                }
                lamp.intensity = intensity * _lit * waver;
                lamp.enabled = _lit > 0.005f;
                lamp.transform.localPosition = _lampHome + lean;
            }

            // Out of play mode the flames are left alone: the editor does not simulate
            // them unless they are selected, and driving their modules would write
            // overrides into every torch in the saved scene.
            if (!Application.isPlaying || flames == null || _rates == null)
                return;
            for (int i = 0; i < flames.Length; ++i)
            {
                ParticleSystem flame = flames[i];
                if (flame == null)
                    continue;
                ParticleSystem.EmissionModule emission = flame.emission;
                emission.rateOverTimeMultiplier = _rates[i] * _lit;
                if (_lit > 0.001f)
                {
                    if (!flame.isPlaying)
                        flame.Play(false);
                }
                else if (flame.isPlaying)
                {
                    // Stop feeding it and let what is burning burn out, rather than
                    // clearing the flame in one frame.
                    flame.Stop(false, ParticleSystemStopBehavior.StopEmitting);
                }
            }
        }
    }
}
