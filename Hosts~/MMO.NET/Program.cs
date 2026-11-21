using Cysharp.Threading;
using Cysharp.Threading.Tasks;
using LiteNetLibManager;
using Microsoft.Extensions.Logging;
using MultiplayerARPG;
using MultiplayerARPG.MMO;
using Newtonsoft.Json;
using System.Runtime.InteropServices;

using var loggerFactory = LoggerFactory.Create(builder =>
{
    builder
        .AddConsole() // log to console
        .SetMinimumLevel(LogLevel.Information);
});

// Read config files
string configFolder = "./Config";
string configFilePath;

bool configFileFound = false;
Dictionary<string, object> serverConfig = new Dictionary<string, object>();

List<string> spawningMapByNames = new List<string>();
List<SpawnAllocateMapByNameData> spawningAllocateMaps = new List<SpawnAllocateMapByNameData>();

configFilePath = configFolder + "/serverConfig.json";
if (File.Exists(configFilePath))
{
    Logging.Log($"Found config file: {configFilePath}");
    configFileFound = true;
    string dataAsJson = File.ReadAllText(configFilePath);
    serverConfig = JsonConvert.DeserializeObject<Dictionary<string, object>>(dataAsJson);
}
else
{
    Logging.Log($"Can't find config file: {configFilePath}");
}

configFilePath = configFolder + "/items.json";
if (File.Exists(configFilePath))
{
    Logging.Log($"Found config file: {configFilePath}");
    string dataAsJson = File.ReadAllText(configFilePath);
    DataManager.Items = JsonConvert.DeserializeObject<Dictionary<int, MinimalItem>>(dataAsJson);
}
else
{
    Logging.Log($"Can't find config file: {configFilePath}");
}

configFilePath = configFolder + "/characterCreationData.json";
if (File.Exists(configFilePath))
{
    Logging.Log($"Found config file: {configFilePath}");
    string dataAsJson = File.ReadAllText(configFilePath);
    DataManager.CharacterCreationData = JsonConvert.DeserializeObject<CharacterCreationData>(dataAsJson);
}
else
{
    Logging.Log($"Can't find config file: {configFilePath}");
}

configFilePath = configFolder + "/socialSystemSetting.json";
if (File.Exists(configFilePath))
{
    Logging.Log($"Found config file: {configFilePath}");
    string dataAsJson = File.ReadAllText(configFilePath);
    SocialSystemSetting replacingConfig = JsonConvert.DeserializeObject<SocialSystemSetting>(dataAsJson);
    if (replacingConfig.GuildMemberRoles != null)
        DatabaseNetworkManager.GuildMemberRoles = replacingConfig.GuildMemberRoles;
    if (replacingConfig.GuildExpTree != null)
        DatabaseNetworkManager.GuildExpTree = replacingConfig.GuildExpTree;
}
else
{
    Logging.Log($"Can't find config file: {configFilePath}");
}

// Prepare server instances
DatabaseNetworkManager databaseNetworkManager = new DatabaseNetworkManager();
CentralNetworkManager centralNetworkManager = new CentralNetworkManager();
LoginNetworkManager loginNetworkManager = new LoginNetworkManager();
MapSpawnNetworkManager mapSpawnNetworkManager = new MapSpawnNetworkManager();

// Database option index
bool useCustomDatabaseClient = false;
ConfigReader.ReadConfigs(serverConfig, ProcessArguments.CONFIG_USE_CUSTOM_DATABASE_CLIENT, out useCustomDatabaseClient, false);
if (ConfigReader.IsArgsProvided(args, ProcessArguments.CONFIG_USE_CUSTOM_DATABASE_CLIENT))
{
    useCustomDatabaseClient = true;
}
serverConfig[ProcessArguments.CONFIG_USE_CUSTOM_DATABASE_CLIENT] = useCustomDatabaseClient;

int dbOptionIndex;
if (ConfigReader.ReadArgs(args, ProcessArguments.ARG_DATABASE_OPTION_INDEX, out dbOptionIndex, 0) ||
    ConfigReader.ReadConfigs(serverConfig, ProcessArguments.CONFIG_DATABASE_OPTION_INDEX, out dbOptionIndex, 0))
{
    if (!useCustomDatabaseClient)
    {
        switch (dbOptionIndex)
        {
            case 0:
                databaseNetworkManager.Database = new SQLiteDatabase();
                break;
            case 1:
                databaseNetworkManager.Database = new MySQLDatabase();
                break;
            case 2:
                databaseNetworkManager.Database = new PostgreSQLDatabase();
                break;
        }
        if (databaseNetworkManager.Database != null)
        {
            // TODO: Setup by files/environment settings
            databaseNetworkManager.Database.UserLoginManager = new DefaultDatabaseUserLogin(new DefaultDatabaseUserLoginConfig()
            {
                PasswordSaltPrefix = string.Empty,
                PasswordSaltPostfix = string.Empty,
            });
        }
    }
}
serverConfig[ProcessArguments.CONFIG_DATABASE_OPTION_INDEX] = dbOptionIndex;

// Database disable cache reading or not?
bool disableDatabaseCaching = false;
#pragma warning disable CS0618 // Type or member is obsolete
ConfigReader.ReadConfigs(serverConfig, ProcessArguments.CONFIG_DATABASE_DISABLE_CACHE_READING, out disableDatabaseCaching, false);
if (ConfigReader.IsArgsProvided(args, ProcessArguments.ARG_DATABASE_DISABLE_CACHE_READING))
{
    disableDatabaseCaching = true;
}
serverConfig[ProcessArguments.CONFIG_DATABASE_DISABLE_CACHE_READING] = disableDatabaseCaching;
#pragma warning restore CS0618 // Type or member is obsolete
ConfigReader.ReadConfigs(serverConfig, ProcessArguments.CONFIG_DISABLE_DATABASE_CACHING, out disableDatabaseCaching, false);
if (ConfigReader.IsArgsProvided(args, ProcessArguments.ARG_DISABLE_DATABASE_CACHING))
{
    disableDatabaseCaching = true;
}
serverConfig[ProcessArguments.CONFIG_DISABLE_DATABASE_CACHING] = disableDatabaseCaching;

// Which cache?
int dbCacheOptionIndex;
if (ConfigReader.ReadArgs(args, ProcessArguments.ARG_DATABASE_CACHE_OPTION_INDEX, out dbCacheOptionIndex, 0) ||
    ConfigReader.ReadConfigs(serverConfig, ProcessArguments.CONFIG_DATABASE_CACHE_OPTION_INDEX, out dbCacheOptionIndex, 0))
{
    if (!disableDatabaseCaching)
    {
        switch (dbCacheOptionIndex)
        {
            case 0:
                databaseNetworkManager.DatabaseCache = new LocalDatabaseCache();
                break;
            case 1:
                databaseNetworkManager.DatabaseCache = new RedisDatabaseCache(loggerFactory.CreateLogger<RedisDatabaseCache>());
                break;
        }
    }
    else
    {
        databaseNetworkManager.DatabaseCache = new DisabledDatabaseCache();
    }
}
serverConfig[ProcessArguments.CONFIG_DATABASE_CACHE_OPTION_INDEX] = dbCacheOptionIndex;

// Use Websocket or not?
bool useWebSocket = false;
ConfigReader.ReadConfigs(serverConfig, ProcessArguments.CONFIG_USE_WEB_SOCKET, out useWebSocket, false);
if (ConfigReader.IsArgsProvided(args, ProcessArguments.ARG_USE_WEB_SOCKET))
{
    useWebSocket = true;
}
serverConfig[ProcessArguments.CONFIG_USE_WEB_SOCKET] = useWebSocket;

// Is websocket running in secure mode or not?
bool webSocketSecure = false;
ConfigReader.ReadConfigs(serverConfig, ProcessArguments.CONFIG_WEB_SOCKET_SECURE, out webSocketSecure, false);
if (ConfigReader.IsArgsProvided(args, ProcessArguments.ARG_WEB_SOCKET_SECURE))
{
    webSocketSecure = true;
}
serverConfig[ProcessArguments.CONFIG_WEB_SOCKET_SECURE] = webSocketSecure;

// Where is the certification file path?
string webSocketCertPath;
if (!ConfigReader.ReadArgs(args, ProcessArguments.ARG_WEB_SOCKET_CERT_PATH, out webSocketCertPath, string.Empty))
{
    ConfigReader.ReadConfigs(serverConfig, ProcessArguments.CONFIG_WEB_SOCKET_CERT_PATH, out webSocketCertPath, string.Empty);
}
serverConfig[ProcessArguments.CONFIG_WEB_SOCKET_CERT_PATH] = webSocketCertPath;

// What is the certification password?
string webSocketCertPassword;
if (!ConfigReader.ReadArgs(args, ProcessArguments.ARG_WEB_SOCKET_CERT_PASSWORD, out webSocketCertPassword, string.Empty))
{
    ConfigReader.ReadConfigs(serverConfig, ProcessArguments.CONFIG_WEB_SOCKET_CERT_PASSWORD, out webSocketCertPassword, string.Empty);
}
serverConfig[ProcessArguments.CONFIG_WEB_SOCKET_CERT_PASSWORD] = webSocketCertPassword;

// Central network address
string centralNetworkAddress;
if (ConfigReader.ReadArgs(args, ProcessArguments.ARG_CENTRAL_ADDRESS, out centralNetworkAddress, mapSpawnNetworkManager.clusterServerAddress) ||
    ConfigReader.ReadConfigs(serverConfig, ProcessArguments.CONFIG_CENTRAL_ADDRESS, out centralNetworkAddress, mapSpawnNetworkManager.clusterServerAddress))
{
    mapSpawnNetworkManager.clusterServerAddress = centralNetworkAddress;
    loginNetworkManager.clusterServerAddress = centralNetworkAddress;
}
serverConfig[ProcessArguments.CONFIG_CENTRAL_ADDRESS] = centralNetworkAddress;

// Central network port
int centralNetworkPort;
if (ConfigReader.ReadArgs(args, ProcessArguments.ARG_CENTRAL_PORT, out centralNetworkPort, centralNetworkManager.networkPort) ||
    ConfigReader.ReadConfigs(serverConfig, ProcessArguments.CONFIG_CENTRAL_PORT, out centralNetworkPort, centralNetworkManager.networkPort))
{
    centralNetworkManager.networkPort = centralNetworkPort;
}
serverConfig[ProcessArguments.CONFIG_CENTRAL_PORT] = centralNetworkPort;

// Open MMORPG: clients log in through a separate login server (CE split it out of Central),
// so the login settings take the place of upstream's central max connections.

// Login network address (the address clients are told to use)
string loginNetworkAddress;
if (ConfigReader.ReadArgs(args, ProcessArguments.ARG_LOGIN_ADDRESS, out loginNetworkAddress, loginNetworkManager.loginServerAddress) ||
    ConfigReader.ReadConfigs(serverConfig, ProcessArguments.CONFIG_LOGIN_ADDRESS, out loginNetworkAddress, loginNetworkManager.loginServerAddress))
{
    loginNetworkManager.loginServerAddress = loginNetworkAddress;
}
serverConfig[ProcessArguments.CONFIG_LOGIN_ADDRESS] = loginNetworkAddress;

// Login network port
int loginNetworkPort;
if (ConfigReader.ReadArgs(args, ProcessArguments.ARG_LOGIN_PORT, out loginNetworkPort, loginNetworkManager.networkPort) ||
    ConfigReader.ReadConfigs(serverConfig, ProcessArguments.CONFIG_LOGIN_PORT, out loginNetworkPort, loginNetworkManager.networkPort))
{
    loginNetworkManager.networkPort = loginNetworkPort;
}
serverConfig[ProcessArguments.CONFIG_LOGIN_PORT] = loginNetworkPort;

// Login max connections: clients on the login and character screens, not players in the game
int loginMaxConnections;
if (!ConfigReader.ReadArgs(args, ProcessArguments.ARG_LOGIN_MAX_CONNECTIONS, out loginMaxConnections, loginNetworkManager.loginMaxConnections) &&
    ConfigReader.ReadConfigs(serverConfig, ProcessArguments.CONFIG_LOGIN_MAX_CONNECTIONS, out loginMaxConnections, loginNetworkManager.loginMaxConnections) &&
    loginMaxConnections < LoginNetworkManager.MinConfiguredMaxConnections)
{
    // Open MMORPG: Unity builds used to write this key without using it, usually as 4, and
    // such a file would let four clients in. The argument is taken as given.
    Logging.LogWarning($"Ignoring `{ProcessArguments.CONFIG_LOGIN_MAX_CONNECTIONS}` = {loginMaxConnections} from the server config: values below {LoginNetworkManager.MinConfiguredMaxConnections} are leftovers of builds that never applied this setting. Using {loginNetworkManager.loginMaxConnections}. Remove the key or raise it; to really go that low, pass {ProcessArguments.ARG_LOGIN_MAX_CONNECTIONS}.");
    loginMaxConnections = loginNetworkManager.loginMaxConnections;
}
loginNetworkManager.loginMaxConnections = loginNetworkManager.maxConnections = loginMaxConnections;
serverConfig[ProcessArguments.CONFIG_LOGIN_MAX_CONNECTIONS] = loginMaxConnections;

// Login max concurrent requests
int loginMaxConcurrentRequest;
if (ConfigReader.ReadArgs(args, ProcessArguments.ARGG_MAX_CONCURRENT_REQUEST, out loginMaxConcurrentRequest, loginNetworkManager.MaxConcurrentRequest) ||
    ConfigReader.ReadConfigs(serverConfig, ProcessArguments.CONFIG_MAX_CONCURRENT_REQUEST, out loginMaxConcurrentRequest, loginNetworkManager.MaxConcurrentRequest))
{
    loginNetworkManager.MaxConcurrentRequest = loginMaxConcurrentRequest;
}
serverConfig[ProcessArguments.CONFIG_MAX_CONCURRENT_REQUEST] = loginMaxConcurrentRequest;

// Open MMORPG: the login server's flood limits (0 turns a limit off). Argument, then
// environment variable or config file, then the default.
LoginFloodProtectionSettings floodProtection = loginNetworkManager.floodProtection;
void ReadLoginSetting(string configName, ref int value)
{
    int read;
    if (ConfigReader.ReadArgs(args, "-" + configName, out read, value) ||
        ConfigReader.ReadConfigs(serverConfig, configName, out read, value))
    {
        value = read;
    }
    serverConfig[configName] = value;
}
ReadLoginSetting(ProcessArguments.CONFIG_LOGIN_MAX_CONNECTIONS_PER_ADDRESS, ref floodProtection.loginMaxConnectionsPerAddress);
ReadLoginSetting(ProcessArguments.CONFIG_LOGIN_MAX_REQUESTS_PER_CONNECTION, ref floodProtection.loginMaxRequestsPerConnection);
ReadLoginSetting(ProcessArguments.CONFIG_LOGIN_REQUEST_WINDOW_SECONDS, ref floodProtection.loginRequestWindowSeconds);
ReadLoginSetting(ProcessArguments.CONFIG_LOGIN_MAX_ATTEMPTS_PER_ADDRESS, ref floodProtection.loginMaxAttemptsPerAddress);
ReadLoginSetting(ProcessArguments.CONFIG_LOGIN_ATTEMPT_WINDOW_SECONDS, ref floodProtection.loginAttemptWindowSeconds);
ReadLoginSetting(ProcessArguments.CONFIG_LOGIN_FAILURES_PER_ADDRESS, ref floodProtection.loginFailuresPerAddress);
ReadLoginSetting(ProcessArguments.CONFIG_LOGIN_FAILURES_PER_USERNAME, ref floodProtection.loginFailuresPerUsername);
ReadLoginSetting(ProcessArguments.CONFIG_LOGIN_FAILURE_WINDOW_SECONDS, ref floodProtection.loginFailureWindowSeconds);
ReadLoginSetting(ProcessArguments.CONFIG_LOGIN_BACKOFF_SECONDS, ref floodProtection.loginBackoffSeconds);
ReadLoginSetting(ProcessArguments.CONFIG_LOGIN_BACKOFF_MAX_SECONDS, ref floodProtection.loginBackoffMaxSeconds);
ReadLoginSetting(ProcessArguments.CONFIG_LOGIN_MAX_QUEUE, ref floodProtection.loginMaxQueue);
ReadLoginSetting(ProcessArguments.CONFIG_REGISTER_MAX_ATTEMPTS_PER_ADDRESS, ref floodProtection.registerMaxAttemptsPerAddress);
ReadLoginSetting(ProcessArguments.CONFIG_REGISTER_ATTEMPT_WINDOW_SECONDS, ref floodProtection.registerAttemptWindowSeconds);
ReadLoginSetting(ProcessArguments.CONFIG_REGISTER_MAX_ACCOUNTS_PER_ADDRESS, ref floodProtection.registerMaxAccountsPerAddress);
ReadLoginSetting(ProcessArguments.CONFIG_REGISTER_ACCOUNT_WINDOW_SECONDS, ref floodProtection.registerAccountWindowSeconds);
bool loginExemptLoopback;
if (ConfigReader.ReadArgs(args, ProcessArguments.ARG_LOGIN_EXEMPT_LOOPBACK, out loginExemptLoopback, floodProtection.loginExemptLoopback) ||
    ConfigReader.ReadConfigs(serverConfig, ProcessArguments.CONFIG_LOGIN_EXEMPT_LOOPBACK, out loginExemptLoopback, floodProtection.loginExemptLoopback))
{
    floodProtection.loginExemptLoopback = loginExemptLoopback;
}
serverConfig[ProcessArguments.CONFIG_LOGIN_EXEMPT_LOOPBACK] = floodProtection.loginExemptLoopback;

// Characters one account may have, 0 for no limit. A Unity server takes it from the Game
// Instance's Max Character Saves; here it is a setting.
ReadLoginSetting(ProcessArguments.CONFIG_MAX_CHARACTERS_PER_ACCOUNT, ref loginNetworkManager.maxCharactersPerAccount);

// Central map spawn timeout (milliseconds)
int mapSpawnMillisecondsTimeout;
if (ConfigReader.ReadArgs(args, ProcessArguments.ARG_MAP_SPAWN_MILLISECONDS_TIMEOUT, out mapSpawnMillisecondsTimeout, centralNetworkManager.mapSpawnMillisecondsTimeout) ||
    ConfigReader.ReadConfigs(serverConfig, ProcessArguments.CONFIG_MAP_SPAWN_MILLISECONDS_TIMEOUT, out mapSpawnMillisecondsTimeout, centralNetworkManager.mapSpawnMillisecondsTimeout))
{
    centralNetworkManager.mapSpawnMillisecondsTimeout = mapSpawnMillisecondsTimeout;
}
serverConfig[ProcessArguments.CONFIG_MAP_SPAWN_MILLISECONDS_TIMEOUT] = mapSpawnMillisecondsTimeout;

// Central - default channels max connections
int defaultChannelMaxConnections;
if (ConfigReader.ReadArgs(args, ProcessArguments.ARG_DEFAULT_CHANNEL_MAX_CONNECTIONS, out defaultChannelMaxConnections, centralNetworkManager.defaultChannelMaxConnections) ||
    ConfigReader.ReadConfigs(serverConfig, ProcessArguments.CONFIG_DEFAULT_CHANNEL_MAX_CONNECTIONS, out defaultChannelMaxConnections, centralNetworkManager.defaultChannelMaxConnections))
{
    centralNetworkManager.defaultChannelMaxConnections = defaultChannelMaxConnections;
}
serverConfig[ProcessArguments.CONFIG_DEFAULT_CHANNEL_MAX_CONNECTIONS] = defaultChannelMaxConnections;

// Central - channels
List<ChannelData> channels;
if (ConfigReader.ReadConfigs(serverConfig, ProcessArguments.CONFIG_CHANNELS, out channels, centralNetworkManager.channels))
{
    centralNetworkManager.channels = channels;
}
serverConfig[ProcessArguments.CONFIG_CHANNELS] = channels;

// Central network port
int clusterNetworkPort;
if (ConfigReader.ReadArgs(args, ProcessArguments.ARG_CLUSTER_PORT, out clusterNetworkPort, centralNetworkManager.clusterServerPort) ||
    ConfigReader.ReadConfigs(serverConfig, ProcessArguments.CONFIG_CLUSTER_PORT, out clusterNetworkPort, centralNetworkManager.clusterServerPort))
{
    centralNetworkManager.clusterServerPort = clusterNetworkPort;
    mapSpawnNetworkManager.clusterServerPort = clusterNetworkPort;
    loginNetworkManager.clusterServerPort = clusterNetworkPort;
}
serverConfig[ProcessArguments.CONFIG_CLUSTER_PORT] = clusterNetworkPort;

// Machine network address, will be set to map spawn / map / chat
string publicNetworkAddress;
if (ConfigReader.ReadArgs(args, ProcessArguments.ARG_PUBLIC_ADDRESS, out publicNetworkAddress, mapSpawnNetworkManager.publicAddress) ||
    ConfigReader.ReadConfigs(serverConfig, ProcessArguments.CONFIG_PUBLIC_ADDRESS, out publicNetworkAddress, mapSpawnNetworkManager.publicAddress))
{
    mapSpawnNetworkManager.publicAddress = publicNetworkAddress;
}
serverConfig[ProcessArguments.CONFIG_PUBLIC_ADDRESS] = publicNetworkAddress;

// Map spawn network port
int mapSpawnNetworkPort;
if (ConfigReader.ReadArgs(args, ProcessArguments.ARG_MAP_SPAWN_PORT, out mapSpawnNetworkPort, mapSpawnNetworkManager.networkPort) ||
    ConfigReader.ReadConfigs(serverConfig, ProcessArguments.CONFIG_MAP_SPAWN_PORT, out mapSpawnNetworkPort, mapSpawnNetworkManager.networkPort))
{
    mapSpawnNetworkManager.networkPort = mapSpawnNetworkPort;
}
serverConfig[ProcessArguments.CONFIG_MAP_SPAWN_PORT] = mapSpawnNetworkPort;

// Map spawn exe path
string spawnExePath;
if (ConfigReader.ReadArgs(args, ProcessArguments.ARG_SPAWN_EXE_PATH, out spawnExePath, mapSpawnNetworkManager.spawnExePath) ||
    ConfigReader.ReadConfigs(serverConfig, ProcessArguments.CONFIG_SPAWN_EXE_PATH, out spawnExePath, mapSpawnNetworkManager.spawnExePath))
{
    mapSpawnNetworkManager.spawnExePath = spawnExePath;
}
if (!File.Exists(spawnExePath))
{
    spawnExePath = System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName;
    mapSpawnNetworkManager.spawnExePath = spawnExePath;
}
serverConfig[ProcessArguments.CONFIG_SPAWN_EXE_PATH] = spawnExePath;

// Map spawn in batch mode
bool notSpawnInBatchMode = mapSpawnNetworkManager.notSpawnInBatchMode = false;
if (ConfigReader.ReadConfigs(serverConfig, ProcessArguments.CONFIG_NOT_SPAWN_IN_BATCH_MODE, out notSpawnInBatchMode, mapSpawnNetworkManager.notSpawnInBatchMode))
{
    mapSpawnNetworkManager.notSpawnInBatchMode = notSpawnInBatchMode;
}
else if (ConfigReader.IsArgsProvided(args, ProcessArguments.ARG_NOT_SPAWN_IN_BATCH_MODE))
{
    mapSpawnNetworkManager.notSpawnInBatchMode = true;
}
serverConfig[ProcessArguments.CONFIG_NOT_SPAWN_IN_BATCH_MODE] = notSpawnInBatchMode;

// Map spawn start port
int spawnStartPort;
if (ConfigReader.ReadArgs(args, ProcessArguments.ARG_SPAWN_START_PORT, out spawnStartPort, mapSpawnNetworkManager.startPort) ||
    ConfigReader.ReadConfigs(serverConfig, ProcessArguments.CONFIG_SPAWN_START_PORT, out spawnStartPort, mapSpawnNetworkManager.startPort))
{
    mapSpawnNetworkManager.startPort = spawnStartPort;
}
serverConfig[ProcessArguments.CONFIG_SPAWN_START_PORT] = spawnStartPort;

// Spawn channels
List<string> spawnChannels;
if (ConfigReader.ReadArgs(args, ProcessArguments.ARG_SPAWN_CHANNELS, out spawnChannels, mapSpawnNetworkManager.spawningChannelIds) ||
    ConfigReader.ReadConfigs(serverConfig, ProcessArguments.CONFIG_SPAWN_CHANNELS, out spawnChannels, mapSpawnNetworkManager.spawningChannelIds))
{
    mapSpawnNetworkManager.spawningChannelIds = spawnChannels;
}
serverConfig[ProcessArguments.CONFIG_SPAWN_CHANNELS] = spawnChannels;

// Spawn maps
List<string> defaultSpawnMapIds = new List<string>();
List<string> spawnMapIds;
if (ConfigReader.ReadArgs(args, ProcessArguments.ARG_SPAWN_MAPS, out spawnMapIds, defaultSpawnMapIds) ||
    ConfigReader.ReadConfigs(serverConfig, ProcessArguments.CONFIG_SPAWN_MAPS, out spawnMapIds, defaultSpawnMapIds))
{
    spawningMapByNames = spawnMapIds;
}
serverConfig[ProcessArguments.CONFIG_SPAWN_MAPS] = spawnMapIds;

// Spawn allocate maps
List<SpawnAllocateMapByNameData> defaultSpawnAllocateMaps = new List<SpawnAllocateMapByNameData>();
List<SpawnAllocateMapByNameData> spawnAllocateMaps;
if (ConfigReader.ReadConfigs(serverConfig, ProcessArguments.CONFIG_SPAWN_ALLOCATE_MAPS, out spawnAllocateMaps, defaultSpawnAllocateMaps))
{
    spawningAllocateMaps = spawnAllocateMaps;
}
serverConfig[ProcessArguments.CONFIG_SPAWN_ALLOCATE_MAPS] = spawnAllocateMaps;

if (!useCustomDatabaseClient)
{
    // Database network address
    string databaseNetworkAddress;
    if (ConfigReader.ReadArgs(args, ProcessArguments.ARG_DATABASE_ADDRESS, out databaseNetworkAddress, databaseNetworkManager.networkAddress) ||
        ConfigReader.ReadConfigs(serverConfig, ProcessArguments.CONFIG_DATABASE_ADDRESS, out databaseNetworkAddress, databaseNetworkManager.networkAddress))
    {
        databaseNetworkManager.networkAddress = databaseNetworkAddress;
    }
    serverConfig[ProcessArguments.CONFIG_DATABASE_ADDRESS] = databaseNetworkAddress;

    // Database network port
    int databaseNetworkPort;
    if (ConfigReader.ReadArgs(args, ProcessArguments.ARG_DATABASE_PORT, out databaseNetworkPort, databaseNetworkManager.networkPort) ||
        ConfigReader.ReadConfigs(serverConfig, ProcessArguments.CONFIG_DATABASE_PORT, out databaseNetworkPort, databaseNetworkManager.networkPort))
    {
        databaseNetworkManager.networkPort = databaseNetworkPort;
    }
    serverConfig[ProcessArguments.CONFIG_DATABASE_PORT] = databaseNetworkPort;
}

if (!configFileFound)
{
    // Write config file
    Logging.Log("Not found server config file, creating a new one");
    if (!Directory.Exists(configFolder))
        Directory.CreateDirectory(configFolder);
    // Upstream writes to configFilePath here, which by now names socialSystemSetting.json
    File.WriteAllText(configFolder + "/serverConfig.json", JsonConvert.SerializeObject(serverConfig, Formatting.Indented));
}

// Read sever start args
bool startingDatabaseServer = false;
if (ConfigReader.IsArgsProvided(args, ProcessArguments.ARG_START_DATABASE_SERVER))
{
    startingDatabaseServer = true;
}
bool startingCentralServer = false;
if (ConfigReader.IsArgsProvided(args, ProcessArguments.ARG_START_CENTRAL_SERVER))
{
    startingCentralServer = true;
}
bool startingMapSpawnServer = false;
if (ConfigReader.IsArgsProvided(args, ProcessArguments.ARG_START_MAP_SPAWN_SERVER))
{
    startingMapSpawnServer = true;
}
bool startingLoginServer = false;
if (ConfigReader.IsArgsProvided(args, ProcessArguments.ARG_START_LOGIN_SERVER))
{
    startingLoginServer = true;
}
if (startingLoginServer && !startingCentralServer)
{
    // The login server reads the central server's channels and cluster server directly
    Logging.LogError("The login server needs the central server in the same process, add " + ProcessArguments.ARG_START_CENTRAL_SERVER);
    startingLoginServer = false;
}

// Start database server
if (useCustomDatabaseClient)
    startingDatabaseServer = false;
if (startingDatabaseServer)
{
    databaseNetworkManager.StartServer();
}

// One database client, shared by the central and login servers
IDatabaseClient databaseClient = null;
if (startingCentralServer || startingLoginServer)
{
    if (useCustomDatabaseClient)
    {
        // RestDatabaseClient reads dbApiUrl/dbSecretKey in Awake, which only Unity calls
        RestDatabaseClient restDatabaseClient = new RestDatabaseClient();
        ConfigReader.ReadConfigs(serverConfig, "dbApiUrl", out restDatabaseClient.apiUrl, restDatabaseClient.apiUrl);
        ConfigReader.ReadConfigs(serverConfig, "dbSecretKey", out restDatabaseClient.secretKey, restDatabaseClient.secretKey);
        Logging.Log("Using the database manager web API at " + restDatabaseClient.apiUrl);
        databaseClient = restDatabaseClient;
    }
    else
    {
        databaseClient = databaseNetworkManager;
        databaseNetworkManager.StartClient();
    }
}

// Start central server
if (startingCentralServer)
{
    centralNetworkManager.useWebSocket = useWebSocket;
    centralNetworkManager.webSocketSecure = webSocketSecure;
    centralNetworkManager.webSocketCertificateFilePath = webSocketCertPath;
    centralNetworkManager.webSocketCertificatePassword = webSocketCertPassword;
    centralNetworkManager.DataManager = new DefaultCentralServerDataManager();
    centralNetworkManager.DatabaseClient = databaseClient;
    centralNetworkManager.StartServer();
}

// Start login server
if (startingLoginServer)
{
    loginNetworkManager.useWebSocket = useWebSocket;
    loginNetworkManager.webSocketSecure = webSocketSecure;
    loginNetworkManager.webSocketCertificateFilePath = webSocketCertPath;
    loginNetworkManager.webSocketCertificatePassword = webSocketCertPassword;
    loginNetworkManager.DataManager = new DefaultLoginServerDataManager();
    loginNetworkManager.DatabaseClient = databaseClient;
    loginNetworkManager.CentralNetworkManager = centralNetworkManager;
    loginNetworkManager.StartServer();
}

// Start map spawn server
if (startingMapSpawnServer)
{
    mapSpawnNetworkManager.spawningMapByNames = spawningMapByNames;
    mapSpawnNetworkManager.spawningAllocateMapByNames = spawningAllocateMaps;
    mapSpawnNetworkManager.StartServer();
}

// Setup process
// Ctrl+C, a closed console, and SIGTERM (systemd, docker stop, a Windows shutdown) end the loop
// below, so the servers stop in order and the database server writes the character saves it is
// still holding. Without this the process just ends, and those saves are lost.
using CancellationTokenSource shutdown = new CancellationTokenSource();
bool stopped = false;
int delay = 1000 / 60;
List<PosixSignalRegistration> signalRegistrations = new List<PosixSignalRegistration>();
foreach (PosixSignal signal in new[] { PosixSignal.SIGINT, PosixSignal.SIGTERM, PosixSignal.SIGQUIT, PosixSignal.SIGHUP })
{
    signalRegistrations.Add(PosixSignalRegistration.Create(signal, context =>
    {
        // Keep the process alive until the loop below has finished stopping the servers
        context.Cancel = true;
        shutdown.Cancel();
    }));
}
AppDomain.CurrentDomain.ProcessExit += CurrentDomain_ProcessExit;
while (!shutdown.IsCancellationRequested)
{
    if (databaseNetworkManager != null && databaseNetworkManager.IsServer)
        databaseNetworkManager.ProcessUpdate();

    if (centralNetworkManager != null && centralNetworkManager.IsServer)
        centralNetworkManager.ProcessUpdate();

    if (loginNetworkManager != null && loginNetworkManager.IsServer)
        loginNetworkManager.ProcessUpdate();

    if (mapSpawnNetworkManager != null && mapSpawnNetworkManager.IsServer)
        mapSpawnNetworkManager.ProcessUpdate();

    // wait for next frame
    await Task.Delay(delay);
}

Logging.Log("Stopping the servers");
await StopServers(TimeSpan.FromSeconds(30));
foreach (PosixSignalRegistration registration in signalRegistrations)
    registration.Dispose();
return;

async Task StopServers(TimeSpan flushTimeout)
{
    if (stopped)
        return;
    stopped = true;

    // Nothing new comes in while the database finishes
    loginNetworkManager.StopHost();
    mapSpawnNetworkManager.StopHost();
    centralNetworkManager.StopHost();

    // The database server defers character saves (DatabaseCharacterSaveScheduler), so the newest
    // state of a character can still be waiting in memory. Write all of it before the process
    // ends. The server keeps receiving meanwhile, for saves that map servers are still sending.
    if (databaseNetworkManager.IsServer)
    {
        DateTime deadline = DateTime.UtcNow + flushTimeout;
        do
        {
            Task flush = databaseNetworkManager.FlushCharacterSavesAsync().AsTask();
            while (!flush.IsCompleted && DateTime.UtcNow < deadline)
            {
                databaseNetworkManager.ProcessUpdate();
                await Task.Delay(delay);
            }
            if (flush.IsFaulted)
                Logging.LogException(nameof(DatabaseNetworkManager), flush.Exception);
            // A save that was already being written when the flush started is not waited for by it
        } while (databaseNetworkManager.PendingCharacterSaveCount > 0 && DateTime.UtcNow < deadline);

        int unsaved = databaseNetworkManager.PendingCharacterSaveCount;
        if (unsaved > 0)
            Logging.LogError(nameof(DatabaseNetworkManager), $"Stopped with {unsaved} character save(s) not written.");
        else
            Logging.Log(nameof(DatabaseNetworkManager), "All character saves are written.");
    }
    databaseNetworkManager.StopHost();
}

void CurrentDomain_ProcessExit(object sender, EventArgs e)
{
    // Only reached with the servers still running when the process ends some other way than the
    // signals above (Environment.Exit, for one). There is little time here, so the wait is short.
    StopServers(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
}