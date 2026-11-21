using System.Collections.Generic;
using Insthync.DevExtension;
using LiteNetLib;
using LiteNetLib.Utils;
using LiteNetLibManager;
using Cysharp.Threading.Tasks;
using System.Net.Sockets;
using System.Collections.Concurrent;

#if (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
using UnityEngine;
using UnityEngine.Serialization;
#endif

namespace MultiplayerARPG.MMO
{
#if (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
    [DefaultExecutionOrder(DefaultExecutionOrders.LOGIN_NETWORK_MANAGER)]
#endif
    public partial class LoginNetworkManager : LiteNetLibManager.LiteNetLibManager, IAppServer
    {

        protected static readonly NetDataWriter s_Writer = new NetDataWriter();

#if (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
        [Header("Central Network Connection")]
#endif
        public string clusterServerAddress = "127.0.0.1";
        public int clusterServerPort = 6010;

#if (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
        [Header("Login Server Settings")]
        [FormerlySerializedAs("machineAddress")]
#endif
        public string loginServerAddress = "127.0.0.1";

#if NET || NETCOREAPP
        // A .NET host has no MMOServerInstance: it sets these itself. The login server runs in
        // the same process as the central server, because it reads its channels and cluster server.
        public IDatabaseClient DatabaseClient { get; set; }
        public ILoginServerDataManager DataManager { get; set; }
        public ClusterClient ClusterClient { get; private set; }
        public CentralNetworkManager CentralNetworkManager { get; set; }
#elif (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
        public IDatabaseClient DatabaseClient
        {
            get { return MMOServerInstance.Singleton.DatabaseClient; }
        }
        public ILoginServerDataManager DataManager { get; set; }
        public ClusterClient ClusterClient { get; private set; }
        private CentralNetworkManager centralNetworkManager = null;
        public CentralNetworkManager CentralNetworkManager
        {
            get
            {
                if (centralNetworkManager == null)
                    centralNetworkManager = MMOServerInstance.Singleton.CentralNetworkManager;
                return centralNetworkManager;
            }
        }
#endif
#if NET || NETCOREAPP || ((UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE)
        private ClusterServer clusterServer = null;
        public ClusterServer ClusterServer
        {
            get
            {
                if (clusterServer == null)
                    clusterServer = CentralNetworkManager.ClusterServer;
                return clusterServer;
            }
        }
#endif

        public string ClusterServerAddress { get { return clusterServerAddress; } }
        public int ClusterServerPort { get { return clusterServerPort; } }
        public string AppAddress { get { return loginServerAddress; } }
        public int AppPort { get { return networkPort; } }
        public string ChannelId { get { return string.Empty; } }
        public string RefId { get { return string.Empty; } }
        public CentralServerPeerType PeerType { get { return CentralServerPeerType.LoginServer; } }

#if NET || NETCOREAPP || ((UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE)
        //Login Queue
        private readonly Queue<LoginQueueEntry> loginQueue = new();
#endif
        private int maxConcurrentRequest = 150;
        public int MaxConcurrentRequest
        {
            get { return maxConcurrentRequest; }
            set { maxConcurrentRequest = value; }
        }
        private int activeLogins = 0;


#if NET || NETCOREAPP || ((UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE)
        // User peers (Login / Register / Manager characters)
        protected readonly Dictionary<long, CentralUserPeerInfo> _userPeers = new Dictionary<long, CentralUserPeerInfo>();
        protected readonly Dictionary<string, CentralUserPeerInfo> _userPeersByUserId = new Dictionary<string, CentralUserPeerInfo>();
        private readonly ConcurrentDictionary<string, CentralServerPeerInfo> _mapServerConnectionIdsBySceneName = new ConcurrentDictionary<string, CentralServerPeerInfo>();
        private readonly ConcurrentDictionary<string, CentralServerPeerInfo> _instanceMapServerConnectionIdsByInstanceId = new ConcurrentDictionary<string, CentralServerPeerInfo>();
#endif

#if (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
        [Header("User Account")]
#endif
        public bool disableDefaultLogin = false;
        public int minUsernameLength = 2;
        public int maxUsernameLength = 24;
        public int minPasswordLength = 2;
        public int minCharacterNameLength = 2;
        public int maxCharacterNameLength = 16;
        public bool requireEmail = false;
        public bool requireEmailVerification = false;
        /// <summary>
        /// How many characters one account may have, 0 for no limit. In Unity it follows
        /// `GameInstance.maxCharacterSaves`, which is also what the client's character list goes by.
        /// </summary>
        public int maxCharactersPerAccount = 5;

#if (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
        [Header("Flood Protection")]
#endif
        public LoginFloodProtectionSettings floodProtection = new LoginFloodProtectionSettings();

#if NET || NETCOREAPP || ((UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE)
        private LoginFloodGuard _floodGuard;
        /// <summary>Applies the `floodProtection` limits. Made on first use and thrown away when the server stops.</summary>
        public LoginFloodGuard FloodGuard
        {
            get
            {
                if (_floodGuard == null)
                    _floodGuard = new LoginFloodGuard(floodProtection);
                return _floodGuard;
            }
            set { _floodGuard = value; }
        }
        private readonly int[] _floodRefusals = new int[LoginFloodGuard.VerdictCount];
        private long _floodReportedAt;
        private const long FloodReportIntervalMilliseconds = 60000;
#endif

        public System.Action onClientConnected;
        public System.Action<DisconnectReason, SocketError, UITextKeys> onClientDisconnected;
        public System.Action onClientStopped;

#if NET || NETCOREAPP
        public LoginNetworkManager() : base()
        {
            Initialize();
        }
#endif

#if (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
        private void Awake()
        {
            DataManager = GetComponentInChildren<ILoginServerDataManager>();
            if (DataManager == null)
            {
                Debug.Log("`DataManager` not setup yet, Use default one...");
                DataManager = new DefaultLoginServerDataManager();
            }
        }

        protected override void Start()
        {
            Initialize();
            base.Start();
        }
#endif
        protected virtual void Initialize()
        {
            useWebSocket = false;
#if NET || NETCOREAPP || ((UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE)
            ClusterClient = new ClusterClient(this);
            ClusterClient.onResponseAppServerRegister = OnResponseAppServerRegister;
            ClusterClient.onResponseAppServerAddress = OnResponseAppServerAddress;
            //Responses
            ClusterClient.RegisterResponseHandler<RequestForceDespawnCharacterMessage, EmptyMessage>(MMORequestTypes.ForceDespawnCharacter);
            ClusterClient.RegisterResponseHandler<RequestCheckChannelsLimit, EmptyMessage>(MMORequestTypes.CheckChannelsLimit);
            ClusterClient.RegisterResponseHandler<RequestFindOnlineUserMessage, ResponseFindOnlineUserMessage>(MMORequestTypes.FindOnlineUser);
            ClusterClient.RegisterResponseHandler<EmptyMessage, ResponseChannelsMessage>(MMORequestTypes.Channels);
#endif
#if (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
            GameInstance.OnGameDataLoadedEvent += GameInstance_OnGameDataLoadedEvent;
#endif
        }

#if (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
        private void GameInstance_OnGameDataLoadedEvent()
        {
            GameInstance.OnGameDataLoadedEvent -= GameInstance_OnGameDataLoadedEvent;
            minCharacterNameLength = GameInstance.Singleton.minCharacterNameLength;
            maxCharacterNameLength = GameInstance.Singleton.maxCharacterNameLength;
            maxCharactersPerAccount = GameInstance.Singleton.maxCharacterSaves;
        }
#endif


        protected override void RegisterMessages()
        {
            EnableRequestResponse(MMOMessageTypes.Request, MMOMessageTypes.Response);
            // Requests
            // Every handler goes through WithRequestLimit, which holds a connection to its request budget
            RegisterRequestToServer(MMORequestTypes.UserLogin, WithRequestLimit<RequestUserLoginMessage, ResponseUserLoginMessage>(HandleRequestUserLogin, () => new ResponseUserLoginMessage() { message = UITextKeys.UI_ERROR_TOO_MANY_REQUESTS }));
            RegisterRequestToServer(MMORequestTypes.UserRegister, WithRequestLimit<RequestUserRegisterMessage, ResponseUserRegisterMessage>(HandleRequestUserRegister, () => new ResponseUserRegisterMessage() { message = UITextKeys.UI_ERROR_TOO_MANY_REQUESTS }));
            RegisterRequestToServer(MMORequestTypes.UserLogout, WithRequestLimit<EmptyMessage, EmptyMessage>(HandleRequestUserLogout, () => EmptyMessage.Value));
            RegisterRequestToServer(MMORequestTypes.Characters, WithRequestLimit<EmptyMessage, ResponseCharactersMessage>(HandleRequestCharacters, () => new ResponseCharactersMessage() { message = UITextKeys.UI_ERROR_TOO_MANY_REQUESTS }));
            RegisterRequestToServer(MMORequestTypes.CreateCharacter, WithRequestLimit<RequestCreateCharacterMessage, ResponseCreateCharacterMessage>(HandleRequestCreateCharacter, () => new ResponseCreateCharacterMessage() { message = UITextKeys.UI_ERROR_TOO_MANY_REQUESTS }));
            RegisterRequestToServer(MMORequestTypes.DeleteCharacter, WithRequestLimit<RequestDeleteCharacterMessage, ResponseDeleteCharacterMessage>(HandleRequestDeleteCharacter, () => new ResponseDeleteCharacterMessage() { message = UITextKeys.UI_ERROR_TOO_MANY_REQUESTS }));
            RegisterRequestToServer(MMORequestTypes.SelectCharacter, WithRequestLimit<RequestSelectCharacterMessage, ResponseSelectCharacterMessage>(HandleRequestSelectCharacter, () => new ResponseSelectCharacterMessage() { message = UITextKeys.UI_ERROR_TOO_MANY_REQUESTS }));
            RegisterRequestToServer(MMORequestTypes.ValidateAccessToken, WithRequestLimit<RequestValidateAccessTokenMessage, ResponseValidateAccessTokenMessage>(HandleRequestValidateAccessToken, () => new ResponseValidateAccessTokenMessage() { message = UITextKeys.UI_ERROR_TOO_MANY_REQUESTS }));
            RegisterRequestToServer(MMORequestTypes.Channels, WithRequestLimit<EmptyMessage, ResponseChannelsMessage>(HandleRequestChannels, () => new ResponseChannelsMessage() { message = UITextKeys.UI_ERROR_TOO_MANY_REQUESTS }));

            // Keeping `RegisterClientMessages` and `RegisterServerMessages` for backward compatibility, can use any of below dev extension methods
            this.InvokeInstanceDevExtMethods("RegisterClientMessages");
            this.InvokeInstanceDevExtMethods("RegisterServerMessages");
            this.InvokeInstanceDevExtMethods("RegisterMessages");
        }

#if NET || NETCOREAPP || ((UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE)
        private void OnResponseAppServerAddress(AckResponseCode responseCode, CentralServerPeerInfo peerInfo)
        {
            if (responseCode != AckResponseCode.Success)
                return;
            switch (peerInfo.peerType)
            {
                case CentralServerPeerType.MapServer:
                    if (!string.IsNullOrEmpty(peerInfo.channelId) && !string.IsNullOrEmpty(peerInfo.refId))
                    {
                        string key = peerInfo.GetPeerInfoKey();
                        if (LogInfo)
                            Logging.Log(LogTag, "Register map server: " + key);
                        _mapServerConnectionIdsBySceneName[key] = peerInfo;
                    }
                    break;
                case CentralServerPeerType.InstanceMapServer:
                    if (!string.IsNullOrEmpty(peerInfo.channelId) && !string.IsNullOrEmpty(peerInfo.refId))
                    {
                        string key = peerInfo.GetPeerInfoKey();
                        if (LogInfo)
                            Logging.Log(LogTag, "Register instance map server: " + key);
                        _instanceMapServerConnectionIdsByInstanceId[key] = peerInfo;
                    }
                    break;
            }
        }
#endif

        /// <summary>
        /// Wraps a request handler so a connection that has used up its request budget is answered
        /// with <paramref name="refusal"/> instead of reaching the handler.
        /// </summary>
        protected RequestDelegate<TRequest, TResponse> WithRequestLimit<TRequest, TResponse>(RequestDelegate<TRequest, TResponse> handler, System.Func<TResponse> refusal)
            where TRequest : INetSerializable, new()
            where TResponse : INetSerializable, new()
        {
#if NET || NETCOREAPP || ((UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE)
            return (requestHandler, request, result) =>
            {
                if (FloodGuard.AllowRequest(requestHandler.ConnectionId) != LoginFloodGuard.Verdict.Allowed)
                {
                    result.InvokeError(refusal());
                    return default;
                }
                return handler(requestHandler, request, result);
            };
#else
            return handler;
#endif
        }

#if NET || NETCOREAPP || ((UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE)
        /// <summary>
        /// The address a client connected from, or null when the transport cannot tell (the
        /// WebSocket transport keeps it to itself). The flood limits then count each such
        /// connection on its own.
        /// </summary>
        protected virtual System.Net.IPAddress GetRemoteAddress(long connectionId)
        {
            LiteNetLibTransport transport = ServerTransport as LiteNetLibTransport;
            if (transport == null || transport.Server == null)
                return null;
            System.Net.IPEndPoint peer = transport.Server.GetPeerById((int)connectionId);
            return peer == null ? null : peer.Address;
        }

        /// <summary>Once a minute, one log line saying how many requests the flood limits turned away.</summary>
        private void ReportFloodRefusals()
        {
            if (_floodGuard == null)
                return;
            long now = _floodGuard.Now;
            if (now - _floodReportedAt < FloodReportIntervalMilliseconds)
                return;
            _floodReportedAt = now;
            if (!_floodGuard.TakeRefusals(_floodRefusals) || !LogWarn)
                return;
            System.Text.StringBuilder text = new System.Text.StringBuilder("Flood protection refused in the last minute:");
            for (int i = 0; i < _floodRefusals.Length; ++i)
            {
                if (_floodRefusals[i] > 0)
                    text.Append(' ').Append((LoginFloodGuard.Verdict)i).Append(' ').Append(_floodRefusals[i]);
            }
            Logging.LogWarning(LogTag, text.ToString());
        }
#endif

        public async void KickClient(long connectionId, byte[] data)
        {
            if (!IsServer)
                return;
            ServerSendPacket(connectionId, 0, DeliveryMethod.ReliableOrdered, MMOMessageTypes.Disconnect, (writer) => writer.PutBytesWithLength(data));
#if NET || NETCOREAPP
            await Task.Delay(500);
#else
            await UniTask.Delay(500);
#endif
            ServerTransport.ServerDisconnect(connectionId);
        }


        protected virtual void Clean()
        {
            this.InvokeInstanceDevExtMethods("Clean");
#if NET || NETCOREAPP || ((UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE)
            _userPeers.Clear();
            _userPeersByUserId.Clear();
            _mapServerConnectionIdsBySceneName.Clear();
            _instanceMapServerConnectionIdsByInstanceId.Clear();
            _floodGuard = null;
#endif
        }

#if NET || NETCOREAPP || ((UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE)
        public override void OnStartServer()
        {
            this.InvokeInstanceDevExtMethods("OnStartServer");
            ClusterClient.OnAppStart();
            base.OnStartServer();
        }
#endif

#if NET || NETCOREAPP || ((UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE)
        public override void OnStopServer()
        {
            ClusterClient.OnAppStop();
            Clean();
            base.OnStopServer();
        }
#endif

        public override void OnStartClient(LiteNetLibClient client)
        {
            this.InvokeInstanceDevExtMethods("OnStartClient", client);
            base.OnStartClient(client);
        }

        public override void OnStopClient()
        {
            if (!IsServer)
                Clean();
            base.OnStopClient();
            if (onClientStopped != null)
                onClientStopped.Invoke();
        }


#if NET || NETCOREAPP || ((UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE)
        protected override void Update()
        {
            base.Update();

            if (IsServer)
            {
                ClusterClient.Update();
                ReportFloodRefusals();
                //Leave this to add LoginNetworkManager main thread actions in future
                if (ClusterClient.IsAppRegistered)
                {

                }

                //if (_mainThreadActions.Count > 0)
                //{
                //    Action tempMainThreadAction;
                //    while (_mainThreadActions.TryDequeue(out tempMainThreadAction))
                //    {
                //        if (tempMainThreadAction != null)
                //            tempMainThreadAction.Invoke();
                //    }
                //}
            }
        }
#endif

        private void OnResponseAppServerRegister(AckResponseCode responseCode)
        {
            if (responseCode != AckResponseCode.Success)
                return;
#if (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE

#endif
        }

        public override void OnClientConnected()
        {
            base.OnClientConnected();
            if (onClientConnected != null)
                onClientConnected.Invoke();
        }

        public override void OnClientDisconnected(DisconnectReason reason, SocketError socketError, byte[] data)
        {
            UITextKeys message = UITextKeys.NONE;
            if (data != null && data.Length > 0)
            {
                NetDataReader reader = new NetDataReader(data);
                message = (UITextKeys)reader.GetPackedUShort();
            }
            if (onClientDisconnected != null)
                onClientDisconnected.Invoke(reason, socketError, message);
        }

        public override void OnPeerConnected(long connectionId)
        {
            base.OnPeerConnected(connectionId);
#if NET || NETCOREAPP || ((UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE)
            // Its address already has its share of connections: drop it
            if (!FloodGuard.ConnectionOpened(connectionId, GetRemoteAddress(connectionId)))
                ServerTransport.ServerDisconnect(connectionId);
#endif
        }

        public override void OnPeerDisconnected(long connectionId, DisconnectReason reason, SocketError socketError)
        {
            RemoveUserPeerByConnectionId(connectionId, out _);
#if NET || NETCOREAPP || ((UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE)
            FloodGuard.ConnectionClosed(connectionId);
#endif
        }

        public void KickClient(long connectionId, UITextKeys message)
        {
            if (!IsServer)
                return;
            s_Writer.Reset();
            s_Writer.PutPackedUShort((ushort)message);
            KickClient(connectionId, s_Writer.CopyData());
        }

        public bool RemoveUserPeerByConnectionId(long connectionId, out CentralUserPeerInfo userPeerInfo)
        {
#if NET || NETCOREAPP || ((UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE)
            if (_userPeers.TryGetValue(connectionId, out userPeerInfo))
            {
                _userPeersByUserId.Remove(userPeerInfo.userId);
                _userPeers.Remove(connectionId);
                return true;
            }
            return false;
#else
            userPeerInfo = default;
            return false;
#endif
        }

        public bool RemoveUserPeerByUserId(string userId, out CentralUserPeerInfo userPeerInfo)
        {
#if NET || NETCOREAPP || ((UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE)
            if (_userPeersByUserId.TryGetValue(userId, out userPeerInfo))
            {
                _userPeersByUserId.Remove(userPeerInfo.userId);
                _userPeers.Remove(userPeerInfo.connectionId);
                return true;
            }
            return false;
#else
            userPeerInfo = default;
            return false;
#endif
        }

#if NET || NETCOREAPP || ((UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE)
        public void KickUser(string userId, UITextKeys message)
        {
            ClusterClient.SendPacket(0, DeliveryMethod.ReliableUnordered, MMOMessageTypes.KickUser, (writer) =>
            {
                writer.Put(userId);
                writer.PutPackedUShort((ushort)message);
            });
        }
#endif

#if NET || NETCOREAPP || ((UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE)
        public void PlayerCharacterRemoved(string userId, string characterId)
        {
            ClusterClient.SendPacket(0, DeliveryMethod.ReliableUnordered, MMOMessageTypes.PlayerCharacterRemoved, (writer) =>
            {
                writer.Put(userId);
                writer.Put(characterId);
            });
        }
#endif


        public async UniTask<List<ChannelEntry>> GetChannels()
        {
#if NET || NETCOREAPP || ((UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE)

            AsyncResponseData<ResponseChannelsMessage> result = await ClusterClient.SendRequestAsync<EmptyMessage, ResponseChannelsMessage>(MMORequestTypes.Channels, new EmptyMessage());

            return result.Response.channels;
#else
            return new List<ChannelEntry>();
#endif
        }

        public async UniTask<bool> MapContainsUser(string userId)
        {
#if NET || NETCOREAPP || ((UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE)
            AsyncResponseData<ResponseFindOnlineUserMessage> result = await ClusterClient.SendRequestAsync<RequestFindOnlineUserMessage, ResponseFindOnlineUserMessage>(MMORequestTypes.FindOnlineUser, new RequestFindOnlineUserMessage()
            {
                userId = userId,
            });
            return result.Response.isFound;
#else
            await UniTask.Yield();
            return false;
#endif

        }

        public async UniTask<bool> ConfirmDespawnCharacter(string userId)
        {
            bool allDone = true;
#if NET || NETCOREAPP || ((UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE)

            AsyncResponseData<EmptyMessage> result = await ClusterClient.SendRequestAsync<RequestForceDespawnCharacterMessage, EmptyMessage>(MMORequestTypes.ForceDespawnCharacter, new RequestForceDespawnCharacterMessage()
            {
                userId = userId,
            });

            allDone = ProcessResponseCode(result.ResponseCode);
            return allDone;
#else
            await UniTask.Yield();
            return false;
#endif

        }

        public async UniTask<bool> ConfirmCanConnectToChannel(string channelId)
        {
            bool canConnect = true;
#if NET || NETCOREAPP || ((UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE)

            AsyncResponseData<EmptyMessage> result = await ClusterClient.SendRequestAsync<RequestCheckChannelsLimit, EmptyMessage>(MMORequestTypes.CheckChannelsLimit, new RequestCheckChannelsLimit()
            {
                channelId = channelId,
            });
            canConnect = ProcessResponseCode(result.ResponseCode);
            return canConnect;
#else
            await UniTask.Yield();
            return false;
#endif

        }

#if NET || NETCOREAPP || ((UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE)

        bool ProcessResponseCode(AckResponseCode responseCode)
        {
            switch (responseCode)
            {
                case AckResponseCode.Success:
                    return true;
                case AckResponseCode.Timeout:
                    // TODO: May tell client what is happening
                    return false;
                case AckResponseCode.Error:
                    // TODO: May tell client what is happening
                    return false;
                case AckResponseCode.Unimplemented:
                    // TODO: May tell client what is happening
                    return false;
                case AckResponseCode.Exception:
                    // TODO: May tell client what is happening
                    return false;
                default
                    :
                    return false;
            }

        }
#endif
    }
}
