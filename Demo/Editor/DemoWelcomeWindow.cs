using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace MultiplayerARPG.Demo.EditorTools
{
    /// <summary>
    /// Shown once, the first time a project opens with the demo in it: the four steps between
    /// importing the kit and walking around the island, the one that is easy to miss being the
    /// map server. The demo runs the kit's MMO flow, where each map is hosted by a separate
    /// process the map spawner launches - a build of this project at <c>builds/OpenMMORPG.exe</c>
    /// beside <c>Assets</c> - so pressing Play gets as far as character select and no further
    /// until that build exists.
    ///
    /// Every button acts only when pressed. Nothing here opens a web page, changes a setting or
    /// starts a build on its own. `Open MMORPG > Demo > Welcome` brings the window back.
    /// </summary>
    [InitializeOnLoad]
    public class DemoWelcomeWindow : EditorWindow
    {
        private const string DocumentationUrl = "https://open-mmorpg.github.io/documentation/";
        private const string SettingsMenu = "Open MMORPG/Install/Import Project Settings";
        private const string DemoDir = "Assets/OpenMMORPG/Demo";
        private const string RenderPipelinePath = DemoDir + "/Settings/DemoURP.asset";
        private const string LowRenderPipelinePath = DemoDir + "/Settings/DemoURP_Low.asset";
        private const string ServerProfilePath = DemoDir + "/Build Profiles/Demo Map Server.asset";
        private const string ClientProfilePath = DemoDir + "/Build Profiles/Demo Client.asset";
        private const string FirstScenePath = DemoDir + "/Scenes/00Init.unity";
        private const string ReadmePath = "Assets/OpenMMORPG/README.md";

        /// <summary>Where the map spawner looks for the map server, relative to the project folder.</summary>
        private const string ServerBuildPath = "builds/OpenMMORPG.exe";

        private Vector2 _scroll;

        static DemoWelcomeWindow()
        {
            if (Application.isBatchMode || EditorPrefs.GetBool(ShownKey))
                return;
            // Not a delayCall: a fresh project goes through several domain reloads while its
            // packages resolve and its scripts compile, and a call queued before the last of them
            // is lost. Wait until the editor has settled instead.
            EditorApplication.update += ShowWhenSettled;
        }

        /// <summary>
        /// EditorPrefs are per machine, not per project, so the key carries the project's path:
        /// a flag shared by every project would show the window in the first and never again.
        /// </summary>
        private static string ShownKey
        {
            get { return "OpenMMORPG.DemoWelcomeShown." + StableHash(Application.dataPath).ToString("X8"); }
        }

        /// <summary>FNV-1a. `string.GetHashCode` is not guaranteed to be the same from one editor session to the next.</summary>
        private static uint StableHash(string text)
        {
            uint hash = 2166136261;
            foreach (char c in text)
                hash = (hash ^ c) * 16777619;
            return hash;
        }

        private static void ShowWhenSettled()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode)
                return;
            EditorApplication.update -= ShowWhenSettled;
            if (EditorPrefs.GetBool(ShownKey))
                return;
            EditorPrefs.SetBool(ShownKey, true);
            Open();
        }

        [MenuItem("Open MMORPG/Demo/Welcome", false, 0)]
        public static void Open()
        {
            var window = GetWindow<DemoWelcomeWindow>(true, "Welcome to Open MMORPG", true);
            window.minSize = new Vector2(560f, 600f);
            Rect main = EditorGUIUtility.GetMainWindowPosition();
            window.position = new Rect(main.x + (main.width - 560f) * 0.5f, main.y + (main.height - 660f) * 0.5f, 560f, 660f);
        }

        private void OnGUI()
        {
            var wrap = new GUIStyle(EditorStyles.label) { wordWrap = true, richText = true };
            var heading = new GUIStyle(EditorStyles.boldLabel) { fontSize = 13, wordWrap = true };

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            GUILayout.Space(8);
            GUILayout.Label("Welcome to Open MMORPG", new GUIStyle(EditorStyles.boldLabel) { fontSize = 16 });
            GUILayout.Label(
                "The demo is a small island and a dungeon running on the kit's MMO servers. Four steps get " +
                "you from here to playing it. The third is the one people miss: each map is hosted by a " +
                "separate build of this project, so until it exists Play stops at character select.", wrap);

            Step(heading, wrap, "1. Import the project settings",
                "Input, physics, tags and layers, quality and time settings the kit expects. Best done " +
                "on a new project, since it replaces those files.");
            if (GUILayout.Button("Import Project Settings", GUILayout.Height(24)))
                EditorApplication.ExecuteMenuItem(SettingsMenu);

            Step(heading, wrap, "2. Use the demo's render settings",
                "The demo is lit for its own Universal Render Pipeline asset, and the sea needs the depth " +
                "and opaque textures it turns on. This also gives the lower half of your Quality levels a " +
                "lighter one (no ambient occlusion, two shadow cascades, hard shadows), so the Quality " +
                "setting does something on a slower machine. Currently in use: <b>" + CurrentPipeline() + "</b>.");
            if (GUILayout.Button("Use Demo Render Pipeline", GUILayout.Height(24)))
                UseDemoPipeline();

            Step(heading, wrap, "3. Build the map server",
                "Needs the <b>Windows Dedicated Server Build Support</b> module (Unity Hub > Installs > " +
                "Add modules). Then in File > Build Profiles: select <b>Demo Map Server</b>, Switch " +
                "Profile, Build, and save it as <b>" + ServerBuildPath + "</b> in the project folder, " +
                "beside Assets. Switch back to <b>Demo Client</b> afterwards: while the server profile is " +
                "active the editor compiles as a server and Play will not work.\n\n" +
                "Rebuild it after changing any scene, entity or game data the server uses.");
            bool built = File.Exists(Path.Combine(Path.GetDirectoryName(Application.dataPath), ServerBuildPath));
            EditorGUILayout.HelpBox(built ? "Map server found at " + ServerBuildPath + "."
                                          : "No map server at " + ServerBuildPath + " yet.",
                                    built ? MessageType.Info : MessageType.Warning);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Open Build Profiles", GUILayout.Height(24)))
                EditorApplication.ExecuteMenuItem("File/Build Profiles");
            if (GUILayout.Button("Show Server Profile", GUILayout.Height(24)))
                Reveal(ServerProfilePath);
            if (GUILayout.Button("Show Client Profile", GUILayout.Height(24)))
                Reveal(ClientProfilePath);
            EditorGUILayout.EndHorizontal();

            Step(heading, wrap, "4. Play",
                "Open the 00Init scene and press Play. It starts the login, central, database and map " +
                "spawn servers in the editor and shows the server list; register an account, make a " +
                "character and start.");
            if (GUILayout.Button("Open 00Init", GUILayout.Height(24)) &&
                EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                EditorSceneManager.OpenScene(FirstScenePath);

            GUILayout.Space(14);
            GUILayout.Label("More", heading);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Show README", GUILayout.Height(24)))
                Reveal(ReadmePath);
            if (GUILayout.Button("Open Documentation (web)", GUILayout.Height(24)))
                Application.OpenURL(DocumentationUrl);
            EditorGUILayout.EndHorizontal();
            GUILayout.Label("This window opens once. Open MMORPG > Demo > Welcome brings it back.", EditorStyles.miniLabel);
            GUILayout.Space(8);
            EditorGUILayout.EndScrollView();
        }

        private static void Step(GUIStyle heading, GUIStyle wrap, string title, string body)
        {
            GUILayout.Space(14);
            GUILayout.Label(title, heading);
            GUILayout.Label(body, wrap);
        }

        private static string CurrentPipeline()
        {
            RenderPipelineAsset current = GraphicsSettings.defaultRenderPipeline;
            if (current == null)
                return "none (Built-in Render Pipeline)";
            return AssetDatabase.GetAssetPath(current) == RenderPipelinePath ? "the demo's" : current.name;
        }

        private static void UseDemoPipeline()
        {
            var pipeline = AssetDatabase.LoadAssetAtPath<RenderPipelineAsset>(RenderPipelinePath);
            if (pipeline == null)
            {
                EditorUtility.DisplayDialog("Render Pipeline Not Found", "Could not find " + RenderPipelinePath + ".", "OK");
                return;
            }
            if (!EditorUtility.DisplayDialog("Use Demo Render Pipeline",
                    "Sets the project's default render pipeline to the demo's URP asset, and gives each Quality " +
                    "level one: the lower half the lighter asset, the rest the full one. You can change it back " +
                    "under Project Settings > Graphics and Quality.",
                    "Use It", "Cancel"))
                return;
            ApplyDemoPipeline();
        }

        /// <summary>
        /// The default pipeline, and a pipeline for every Quality level: the lower half of the levels get
        /// `DemoURP_Low`, the rest `DemoURP`. Six levels are Very Low to Medium on the lighter one.
        /// </summary>
        internal static void ApplyDemoPipeline()
        {
            var full = AssetDatabase.LoadAssetAtPath<RenderPipelineAsset>(RenderPipelinePath);
            var low = AssetDatabase.LoadAssetAtPath<RenderPipelineAsset>(LowRenderPipelinePath);
            if (full == null)
                return;
            GraphicsSettings.defaultRenderPipeline = full;
            int levels = QualitySettings.names.Length;
            int previous = QualitySettings.GetQualityLevel();
            for (int i = 0; i < levels; ++i)
            {
                QualitySettings.SetQualityLevel(i, false);
                QualitySettings.renderPipeline = i < levels / 2 && low != null ? low : full;
            }
            QualitySettings.SetQualityLevel(previous, false);
            AssetDatabase.SaveAssets();
        }

        private static void Reveal(string path)
        {
            Object asset = AssetDatabase.LoadMainAssetAtPath(path);
            if (asset == null)
                return;
            Selection.activeObject = asset;
            EditorGUIUtility.PingObject(asset);
        }
    }
}
