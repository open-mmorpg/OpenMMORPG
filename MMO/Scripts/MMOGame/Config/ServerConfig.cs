// CE security: #31

using System.Collections.Generic;

namespace MultiplayerARPG.MMO
{
    public partial class ServerConfig
    {
        // Login server
        public string LoginAddress;
        public int? LoginPort;
        public int? loginMaxConnections;
        public int? MaxConcurrentRequest;

        // Login server flood protection (0 turns a limit off)
        public int? loginMaxConnectionsPerAddress;
        public int? loginMaxRequestsPerConnection;
        public int? loginRequestWindowSeconds;
        public int? loginMaxAttemptsPerAddress;
        public int? loginAttemptWindowSeconds;
        public int? loginFailuresPerAddress;
        public int? loginFailuresPerUsername;
        public int? loginFailureWindowSeconds;
        public int? loginBackoffSeconds;
        public int? loginBackoffMaxSeconds;
        public int? loginMaxQueue;
        public int? registerMaxAttemptsPerAddress;
        public int? registerAttemptWindowSeconds;
        public int? registerMaxAccountsPerAddress;
        public int? registerAccountWindowSeconds;
        public bool? loginExemptLoopback;

        // Central server
        public string centralAddress;
        public int? centralPort;
        public int? centralMaxConnections;

        // Central channel
        public int? mapSpawnMillisecondsTimeout;
        public int? defaultChannelMaxConnections;
        public List<ChannelData> channels;

        // Central web-socket connection (for login/character management)
        public bool? useWebSocket;
        public bool? webSocketSecure;
        public string webSocketCertPath;
        public string webSocketCertPassword;

        // The secret which every server of one deployment shares, see `ServerSecret`
        public string serverSecret;

        // Cluster server
        public int? clusterPort;
        [System.Obsolete("Use `publicAddress` instead.")]
        public string machineAddress;
        public string publicAddress;

        // Map spawn server
        public int? mapSpawnPort;
        public string spawnExePath;
        public bool? notSpawnInBatchMode;
        public int? spawnStartPort;
        public List<string> spawnMaps;
        public List<string> spawnChannels;
        public List<SpawnAllocateMapByNameData> spawnAllocateMaps;

        // Map server
        public int? mapPort;
        public int? mapMaxConnections;

        // Database manager server
        public bool? useCustomDatabaseClient;
        public int? databaseOptionIndex;
        [System.Obsolete("Use `disableDatabaseCaching` instead.")]
        public bool? databaseDisableCacheReading;
        public bool? disableDatabaseCaching;

        public string databaseManagerAddress;
        public int? databaseManagerPort;
        // Rounds of the password hash (1000 unless set)
        public int? passwordHashIterations;
    }
}