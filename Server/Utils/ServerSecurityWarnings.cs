using LiteNetLibManager;
using System.Collections.Generic;

namespace MultiplayerARPG.MMO
{
    /// <summary>
    /// What a server says at start about settings which were left at the values the kit ships with.
    /// The functions return the text (or nothing), so they can be checked without a running server.
    /// </summary>
    public static class ServerSecurityWarnings
    {
        public const string DefaultConnectKey = "SampleConnectKey";
        public const string DefaultDatabaseApiKey = "secret";

        /// <summary>
        /// About a player-facing server (login, map) whose transport still has the connect key the kit ships with.
        /// </summary>
        public static string ForConnectKey(string serverName, ITransport transport)
        {
            LiteNetLibTransport liteNetLibTransport = transport as LiteNetLibTransport;
            if (liteNetLibTransport == null)
                return null;
            return ForConnectKey(serverName, liteNetLibTransport.ConnectKey);
        }

        public static string ForConnectKey(string serverName, string connectKey)
        {
            if (connectKey != DefaultConnectKey)
                return null;
            return $"The {serverName} server's connect key is still `{DefaultConnectKey}`, the one every copy of the kit ships with. Change `connectKey` on its transport factory in your project. It is not a secret: it ships inside your client build, so it only turns away clients which were not built from your project, and your server and client builds must have the same one.";
        }

        /// <summary>
        /// About the database web API's key, on either side of it.
        /// </summary>
        public static string ForDatabaseApiKey(string apiKey)
        {
            if (apiKey != DefaultDatabaseApiKey)
                return null;
            return $"The database web API's key is still `{DefaultDatabaseApiKey}`, the one every copy of the kit ships with. Anyone who can reach the API can read and change every account with it. Change `ApiSecretKey` in the API's appsettings.json and `dbSecretKey` on the servers which use it.";
        }

        public static void Log(string logTag, List<string> warnings)
        {
            if (warnings == null)
                return;
            for (int i = 0; i < warnings.Count; ++i)
            {
                Log(logTag, warnings[i]);
            }
        }

        public static void Log(string logTag, string warning)
        {
            if (!string.IsNullOrEmpty(warning))
                Logging.LogWarning(logTag, warning);
        }
    }
}
