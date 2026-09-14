using UnityEngine;

namespace MultiplayerARPG.Demo
{
    /// <summary>
    /// Turns the kit's time of day into a sky, a sun and an ambient.
    ///
    /// The kit ticks a clock on the server and replicates it, but nothing in it draws the
    /// result: <c>SampleDayNightTimeApplyer</c> moves the light and sets the ambient, and
    /// leaves the sky alone entirely — so with a fixed skybox the sun sets, the shadows
    /// go, and the sky stays broad daylight overhead. This drives the sky as well, which
    /// is the part a player actually looks at.
    ///
    /// Everything here is presentation. The clock itself is the server's, so every client
    /// reads the same hour and arrives at the same sky without any of this being sent.
    /// </summary>
    [ExecuteAlways]
    public class DemoSkyCycle : MonoBehaviour
    {
        [Header("What it drives")]
        public Light sun;
        public Material sky;

        [Header("The day")]
        [Tooltip("Hour the sun clears the horizon, and the hour it goes back under.")]
        [Range(0f, 12f)] public float sunrise = 6f;
        [Range(12f, 24f)] public float sunset = 19f;
        [Tooltip("How long dawn and dusk take, in hours. The sky blends across this.")]
        [Range(0.25f, 4f)] public float twilight = 1.5f;

        [Header("Colours through the day")]
        public Gradient sunColour;
        public Gradient skyAmbient;
        public Gradient horizonAmbient;
        public Gradient groundAmbient;
        public Gradient fog;

        [Header("Strength")]
        public float noonIntensity = 1.15f;
        public float nightIntensity = 0.12f;

        [Header("Editing")]
        [Tooltip("The hour shown while the game is not running, so the scene can be lit and looked at.")]
        [Range(0f, 24f)] public float previewHour = 12f;

        private static readonly int BlendId = Shader.PropertyToID("_Blend");

        /// <summary>The blend the environment probe was last built from.</summary>
        private float _litFor = -1f;

        /// <summary>The hour the world is at: the server's while playing, the preview otherwise.</summary>
        public float Hour
        {
            get
            {
                if (!Application.isPlaying)
                    return previewHour;
                BaseGameNetworkManager manager = BaseGameNetworkManager.Singleton;
                if (manager == null || !manager.IsNetworkActive)
                    return previewHour;
                BaseDayNightTimeUpdater clock = GameInstance.Singleton.DayNightTimeUpdater;
                return clock == null ? previewHour : clock.TimeOfDay;
            }
        }

        private void Update()
        {
            Apply(Hour);
        }

        public void Apply(float hour)
        {
            float night = NightAmount(hour);
            float through = hour / 24f;

            if (sky != null && sky.HasProperty(BlendId))
            {
                sky.SetFloat(BlendId, night);
                // Unity builds the environment reflection from the sky once and keeps it.
                // Changing the sky material does not rebuild it, so anything reflective
                // goes on mirroring whatever sky was up when it was last built - which is
                // why the sea lay there at midnight as a bright band of noon. Rebuilding
                // is not cheap, so it is done only when the sky has actually moved on.
                if (Mathf.Abs(night - _litFor) > 0.02f)
                {
                    _litFor = night;
                    DynamicGI.UpdateEnvironment();
                }
            }

            if (sun != null)
            {
                // Turned about the horizon so that it rises at sunrise and sets at sunset
                // rather than at six and eighteen, and kept off due east so the shadows
                // fall across the island rather than straight down it.
                float above = Mathf.InverseLerp(sunrise, sunset, hour);
                sun.transform.rotation = Quaternion.Euler(above * 180f, 138f, 0f);
                sun.color = sunColour.Evaluate(through);
                sun.intensity = Mathf.Lerp(noonIntensity, nightIntensity, night);
                // A light below the horizon still lights the world in Unity, so it is
                // switched off rather than left shining upward through the ground.
                sun.enabled = hour > sunrise - twilight && hour < sunset + twilight;
            }

            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = skyAmbient.Evaluate(through);
            RenderSettings.ambientEquatorColor = horizonAmbient.Evaluate(through);
            RenderSettings.ambientGroundColor = groundAmbient.Evaluate(through);
            RenderSettings.fogColor = fog.Evaluate(through);
        }

        /// <summary>
        /// How far into night it is: 0 in full day, 1 in full night, and sliding across
        /// the twilight either side. This is what the sky blends on.
        /// </summary>
        public float NightAmount(float hour)
        {
            float dawn = Mathf.InverseLerp(sunrise - twilight, sunrise + twilight, hour);
            float dusk = Mathf.InverseLerp(sunset - twilight, sunset + twilight, hour);
            // Full night before dawn finishes and after dusk begins; day in between.
            return 1f - Mathf.Clamp01(dawn - dusk);
        }
    }
}
