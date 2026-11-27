// ce scability: #53

using System;
using System.Collections.Concurrent;
using System.Linq;
using Insthync.DevExtension;
using LiteNetLibManager;
using Cysharp.Threading.Tasks;
using System.Collections.Generic;
using System.Net.Sockets;
using LiteNetLib;
using LiteNetLib.Utils;

#if (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
using UnityEngine;
#endif

namespace MultiplayerARPG.MMO
{
#if (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
    [DefaultExecutionOrder(DefaultExecutionOrders.DATABASE_NETWORK_MANAGER)]
#endif
    public partial class DatabaseNetworkManager : LiteNetLibManager.LiteNetLibManager
    {
#if (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
        [SerializeField]
#endif
        private BaseDatabase database = null;
#if (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
        [SerializeField]
#endif
        private BaseDatabase[] databaseOptions = new BaseDatabase[0];

        public BaseDatabase Database
        {
            get
            {
                return database == null ? databaseOptions.FirstOrDefault() : database;
            }
            set
            {
                database = value;
            }
        }

        public IDatabaseCache DatabaseCache
        {
            get; set;
        }

#if (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
        [SerializeField]
#endif
        private bool useDeferredCharacterSaveScheduler = true;
#if (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
        [SerializeField]
#endif
        private int characterSaveLaneCount = 32;
#if (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
        [SerializeField]
#endif
        private int characterSaveTickMilliseconds = 500;
#if (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
        [SerializeField]
#endif
        private int characterSaveMaxPerTick = 8;
#if (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
        [SerializeField]
#endif
        private float characterSaveMinIntervalSeconds = 20f;
#if (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
        [SerializeField]
#endif
        private float characterSaveRetryDelaySeconds = 5f;

#if NET || NETCOREAPP || ((UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE)
        // Password hashing runs here, beside the loop; `Update` hands the results back on it
        public OffLoopWorkQueue BackgroundWork { get; } = new OffLoopWorkQueue();
#endif

        // Rounds of the password hash, see `DefaultDatabaseUserLogin`
        public int passwordHashIterations = DefaultDatabaseUserLogin.DefaultIterations;

        private DatabaseCharacterSaveScheduler _characterSaveScheduler;
        private readonly ConcurrentDictionary<string, List<PlayerCharacterData>> _charactersByUserIdCache = new ConcurrentDictionary<string, List<PlayerCharacterData>>();


        public bool UseDeferredCharacterSaveScheduler => useDeferredCharacterSaveScheduler;

        public bool ProceedingBeforeQuit { get; private set; } = false;
        public bool ReadyToQuit { get; private set; } = false;

        public void SetDatabaseByOptionIndex(int index)
        {
            if (databaseOptions != null &&
                databaseOptions.Length > 0 &&
                index >= 0 &&
                index < databaseOptions.Length)
                database = databaseOptions[index];
        }

#if NET || NETCOREAPP
        public DatabaseNetworkManager() : base()
        {
            Initialize();
        }
#endif

#if (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
        protected override void Start()
        {
            Initialize();
            base.Start();
        }

        protected override void OnApplicationQuit()
        {
            // Stopping Play in the editor never runs ProceedBeforeQuit, so write the deferred saves here.
            // SQLite with the local cache finishes synchronously, a remote database is best effort.
            if (_characterSaveScheduler != null && _characterSaveScheduler.PendingCount > 0)
                FlushCharacterSavesAsync().Forget();
            base.OnApplicationQuit();
        }
#endif

        protected virtual void Initialize()
        {
            useWebSocket = false;
            maxConnections = int.MaxValue;
        }

        public async void ProceedBeforeQuit()
        {
            if (ProceedingBeforeQuit)
                return;
            ProceedingBeforeQuit = true;
            // Delay 30 secs before quit
            int seconds = 30;
            do
            {
                Logging.Log($"[DatabaseNetworkManager] {seconds} seconds before quit.");
#if (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
                await UniTask.Delay(1000);
#elif NET || NETCOREAPP
                await Task.Delay(1000);
#endif
                seconds--;
            } while (seconds > 0);
#if NET || NETCOREAPP || ((UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE)
            await FlushCharacterSavesAsync();
#endif
            await UniTask.Yield();
            ReadyToQuit = true;
            // Request to quit again
#if (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
            Application.Quit();
#endif
        }

        public override async void OnStartServer()
        {
            base.OnStartServer();
#if (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
            Database.Initialize();
#endif
#if NET || NETCOREAPP || ((UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE)
            PrepareDatabase();
            await Database.DoMigration();

            if (useDeferredCharacterSaveScheduler)
            {
                _characterSaveScheduler = new DatabaseCharacterSaveScheduler(
                    this,
                    characterSaveLaneCount,
                    characterSaveTickMilliseconds,
                    characterSaveMaxPerTick,
                    characterSaveMinIntervalSeconds,
                    characterSaveRetryDelaySeconds);
                _characterSaveScheduler.Start();

                Logging.Log(nameof(DatabaseNetworkManager),
                    $"Character save scheduler started. lanes={characterSaveLaneCount}, tickMs={characterSaveTickMilliseconds}, maxPerTick={characterSaveMaxPerTick}, minInterval={characterSaveMinIntervalSeconds}s");
            }
#endif
        }

#if NET || NETCOREAPP || ((UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE)
        protected void PrepareDatabase()
        {
            Database.BackgroundWork = BackgroundWork;
            ApplyPasswordHashIterations();
        }

        protected override void Update()
        {
            base.Update();
            BackgroundWork.Update();
        }

        protected void ApplyPasswordHashIterations()
        {
            if (passwordHashIterations > 0 && Database.UserLoginManager is DefaultDatabaseUserLogin userLogin)
                userLogin.Iterations = passwordHashIterations;
        }

        public void EnqueueCharacterSave(UpdateCharacterReq request, bool forceImmediate = false)
        {
            if (_characterSaveScheduler == null)
                return;

            _characterSaveScheduler.Enqueue(request, forceImmediate);
        }

        public async UniTask CancelCharacterSaveAsync(string characterId)
        {
            if (_characterSaveScheduler != null)
                await _characterSaveScheduler.CancelAsync(characterId);
        }


        public async UniTask<bool> InternalPersistCharacterUpdate(UpdateCharacterReq request)
        {
            try
            {
                await Database.UpdateCharacter(
                    request.State,
                    request.CharacterData,
                    request.SummonBuffs,
                    request.DeleteStorageReservation);

                await UniTask.WhenAll(
                    DatabaseCache.SetPlayerCharacter(request.CharacterData),
                    DatabaseCache.SetSummonBuffs(request.CharacterData.Id, request.SummonBuffs));

                return true;
            }
            catch (Exception ex)
            {
                Logging.LogError(nameof(DatabaseNetworkManager),
                    $"Deferred character save failed for characterId={request.CharacterData.Id}. {ex}");
                return false;
            }
        }


        public async UniTask FlushCharacterSavesAsync()
        {
            if (_characterSaveScheduler != null)
                await _characterSaveScheduler.FlushAllAsync();
        }

        /// <summary>Character saves the scheduler still holds: waiting to be written, or being written now.</summary>
        public int PendingCharacterSaveCount
        {
            get { return _characterSaveScheduler != null ? _characterSaveScheduler.PendingCount : 0; }
        }

        public bool TryGetCharactersByUserIdFromCache(string userId, out List<PlayerCharacterData> characters)
        {
            if (_charactersByUserIdCache.TryGetValue(userId, out characters))
            {
                if (characters == null)
                {
                    characters = new List<PlayerCharacterData>();
                    return true;
                }
                characters = new List<PlayerCharacterData>(characters);
                return true;
            }
            characters = null;
            return false;
        }

        public void SetCharactersByUserIdCache(string userId, List<PlayerCharacterData> characters)
        {
            if (string.IsNullOrEmpty(userId))
                return;

            _charactersByUserIdCache[userId] = characters == null
                ? new List<PlayerCharacterData>()
                : new List<PlayerCharacterData>(characters);
        }

        public void InvalidateCharactersByUserIdCache(string userId)
        {
            if (string.IsNullOrEmpty(userId))
                return;

            _charactersByUserIdCache.TryRemove(userId, out _);
        }

        public void UpsertCharacterInUserIdCache(PlayerCharacterData characterData)
        {
            if (characterData == null || string.IsNullOrEmpty(characterData.UserId) || string.IsNullOrEmpty(characterData.Id))
                return;

            _charactersByUserIdCache.AddOrUpdate(
                characterData.UserId,
                _ => new List<PlayerCharacterData>() { characterData },
                (_, existing) =>
                {
                    List<PlayerCharacterData> list = existing == null
                        ? new List<PlayerCharacterData>()
                        : new List<PlayerCharacterData>(existing);

                    int index = list.FindIndex(c => c != null && c.Id == characterData.Id);
                    if (index >= 0)
                        list[index] = characterData;
                    else
                        list.Add(characterData);

                    return list;
                });
        }

        public void RemoveCharacterFromUserIdCache(string userId, string characterId)
        {
            if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(characterId))
                return;

            _charactersByUserIdCache.AddOrUpdate(
                userId,
                _ => new List<PlayerCharacterData>(),
                (_, existing) =>
                {
                    List<PlayerCharacterData> list = existing == null
                        ? new List<PlayerCharacterData>()
                        : new List<PlayerCharacterData>(existing);

                    list.RemoveAll(c => c != null && c.Id == characterId);
                    return list;
                });
        }
#endif

#if NET || NETCOREAPP || ((UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE)
        /// <summary>
        /// Client: set while connected, tells whether the database server accepted this server
        /// </summary>
        private UniTaskCompletionSource<bool> _clientAuthentication;
        private bool _clientAuthenticationAnswered;

        protected override void OnServerUpdate(LogicUpdater updater)
        {
            base.OnServerUpdate(updater);
            DatabaseServerHandler serverHandler = Server as DatabaseServerHandler;
            if (serverHandler != null)
                serverHandler.DisconnectUnauthenticatedPeers();
        }

        public override void OnClientConnected()
        {
            base.OnClientConnected();
            // Requests wait for the answer to this one, see `WaitForServerAuthentication`
            UniTaskCompletionSource<bool> authentication = new UniTaskCompletionSource<bool>();
            _clientAuthentication = authentication;
            _clientAuthenticationAnswered = false;
            AuthenticateToServer(authentication).Forget();
        }

        public override void OnClientDisconnected(DisconnectReason reason, SocketError socketError, byte[] data)
        {
            base.OnClientDisconnected(reason, socketError, data);
            UniTaskCompletionSource<bool> authentication = _clientAuthentication;
            _clientAuthentication = null;
            if (authentication != null)
            {
                if (!_clientAuthenticationAnswered && reason == DisconnectReason.RemoteConnectionClose)
                    Logging.LogError(nameof(DatabaseNetworkManager), $"The database server closed the connection before it accepted this server. The database server's log has the reason. If it runs on another machine, both need the same `{ProcessArguments.CONFIG_SERVER_SECRET}`.");
                authentication.TrySetResult(false);
            }
        }

        /// <summary>
        /// Client: prove to the database server that this server has the deployment's secret
        /// </summary>
        private async UniTaskVoid AuthenticateToServer(UniTaskCompletionSource<bool> authentication)
        {
            AsyncResponseData<EmptyMessage> response = await Client.SendRequestAsync<DbServerAuthenticateMessage, EmptyMessage>(
                DatabaseRequestTypes.ServerAuthenticate,
                DbServerAuthenticateMessage.Create(ServerSecret.Value, ServerSecret.Now));
            bool accepted;
            if (_clientAuthentication == authentication)
                _clientAuthenticationAnswered = true;
            switch (response.ResponseCode)
            {
                case AckResponseCode.Success:
                    accepted = true;
                    Logging.Log(nameof(DatabaseNetworkManager), "The database server accepted this server");
                    break;
                case AckResponseCode.Unimplemented:
                    // A database server from before the secret existed, it serves anyone
                    accepted = true;
                    if (ServerSecret.IsSet)
                        Logging.LogWarning(nameof(DatabaseNetworkManager), $"The database server is an older build which does not check `{ProcessArguments.CONFIG_SERVER_SECRET}`, so its port is open to anyone who can reach it. Update it.");
                    break;
                default:
                    accepted = false;
                    Logging.LogError(nameof(DatabaseNetworkManager), $"The database server did not accept this server ({response.ResponseCode}). Give this server the same `{ProcessArguments.CONFIG_SERVER_SECRET}` as the database server, and keep both machines' clocks within {ServerSecret.MaxClockDifferenceSeconds} seconds. The database server's log has the reason.");
                    break;
            }
            authentication.TrySetResult(accepted);
        }

        /// <summary>
        /// Client: wait until the database server answered <see cref="AuthenticateToServer"/>, returns `FALSE` if it refused.
        /// A request sent before that answer could overtake it and be dropped by the server.
        /// </summary>
        private async UniTask<bool> WaitForServerAuthentication(string functionName)
        {
            UniTaskCompletionSource<bool> authentication = _clientAuthentication;
            if (authentication == null)
            {
                // Not connected, the request fails by itself
                return true;
            }
            if (await authentication.Task)
                return true;
            Logging.LogError(nameof(DatabaseNetworkManager), $"Cannot {functionName}: the database server did not accept this server.");
            return false;
        }
#endif

        /// <summary>
        /// Server: a connecting server proves that it has the deployment's secret, its other requests are not read before this one passed.
        /// </summary>
        protected UniTaskVoid HandleServerAuthenticate(RequestHandlerData requestHandler, DbServerAuthenticateMessage request, RequestProceedResultDelegate<EmptyMessage> result)
        {
#if NET || NETCOREAPP || ((UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE)
            long connectionId = requestHandler.ConnectionId;
            DatabaseServerHandler serverHandler = Server as DatabaseServerHandler;
            if (serverHandler == null)
            {
                result.InvokeError(EmptyMessage.Value);
                return default;
            }
            ServerPeerGate gate = serverHandler.Gate;
            if (!ServerSecret.IsSet)
            {
                // Nothing to prove, the gate lets a connection from this machine in as it connects
                if (!string.IsNullOrEmpty(request.hash))
                    Logging.LogWarning(nameof(DatabaseNetworkManager), $"Connection {connectionId} has a `{ProcessArguments.CONFIG_SERVER_SECRET}` but this database server has none. Set the same here, until then this port serves this machine only.");
                if (gate.IsAuthorized(connectionId))
                    result.InvokeSuccess(EmptyMessage.Value);
                else
                    result.InvokeError(EmptyMessage.Value);
                return default;
            }
            long now = ServerSecret.Now;
            ServerSecretCheck check = request.Check(ServerSecret.Value, now);
            if (check == ServerSecretCheck.Valid && !gate.TryUseProof(request.hash, request.time, now))
                check = ServerSecretCheck.Replayed;
            if (check == ServerSecretCheck.Valid)
            {
                gate.Authorize(connectionId);
                result.InvokeSuccess(EmptyMessage.Value);
                return default;
            }
            string reason;
            if (check == ServerSecretCheck.Replayed)
                reason = "this exact proof was accepted before. Someone is replaying a captured one.";
            else if (check == ServerSecretCheck.WrongTime)
                reason = $"its clock differs from this server's by {Math.Abs(now - request.time)} seconds, the limit is {ServerSecret.MaxClockDifferenceSeconds}. Synchronise the clocks of both machines.";
            else
                reason = $"it does not have this server's `{ProcessArguments.CONFIG_SERVER_SECRET}`. Every server of one deployment must be given the same one.";
            Logging.LogError(nameof(DatabaseNetworkManager), $"Refused connection {connectionId}: {reason}");
            result.InvokeError(EmptyMessage.Value);
#endif
            return default;
        }

        protected override void RegisterMessages()
        {
#if NET || NETCOREAPP || ((UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE)
            // Every packet to the database server passes a gate before any handler sees it, see `DatabaseServerHandler`.
            // The handlers below, and the ones addons add, are registered to it
            Server = new DatabaseServerHandler(this);
#endif
            base.RegisterMessages();
            EnableRequestResponse(MMOMessageTypes.Request, MMOMessageTypes.Response);
            RegisterRequestToServer<DbServerAuthenticateMessage, EmptyMessage>(DatabaseRequestTypes.ServerAuthenticate, HandleServerAuthenticate);
            RegisterRequestToServer<DbRequestMessage<ValidateUserLoginReq>, ValidateUserLoginResp>(DatabaseRequestTypes.ValidateUserLogin, ValidateUserLogin);
            RegisterRequestToServer<DbRequestMessage<ValidateAccessTokenReq>, ValidateAccessTokenResp>(DatabaseRequestTypes.ValidateAccessToken, ValidateAccessToken);
            RegisterRequestToServer<DbRequestMessage<GetUserLevelReq>, GetUserLevelResp>(DatabaseRequestTypes.GetUserLevel, GetUserLevel);
            RegisterRequestToServer<DbRequestMessage<GetGoldReq>, GoldResp>(DatabaseRequestTypes.GetGold, GetGold);
            RegisterRequestToServer<DbRequestMessage<ChangeGoldReq>, GoldResp>(DatabaseRequestTypes.ChangeGold, ChangeGold);
            RegisterRequestToServer<DbRequestMessage<GetCashReq>, CashResp>(DatabaseRequestTypes.GetCash, GetCash);
            RegisterRequestToServer<DbRequestMessage<ChangeCashReq>, CashResp>(DatabaseRequestTypes.ChangeCash, ChangeCash);
            RegisterRequestToServer<DbRequestMessage<UpdateAccessTokenReq>, EmptyMessage>(DatabaseRequestTypes.UpdateAccessToken, UpdateAccessToken);
            RegisterRequestToServer<DbRequestMessage<CreateUserLoginReq>, EmptyMessage>(DatabaseRequestTypes.CreateUserLogin, CreateUserLogin);
            RegisterRequestToServer<DbRequestMessage<FindUsernameReq>, FindUsernameResp>(DatabaseRequestTypes.FindUsername, FindUsername);
            RegisterRequestToServer<DbRequestMessage<CreateCharacterReq>, CharacterResp>(DatabaseRequestTypes.CreateCharacter, CreateCharacter);
            RegisterRequestToServer<DbRequestMessage<GetCharacterReq>, CharacterResp>(DatabaseRequestTypes.GetCharacter, GetCharacter);
            RegisterRequestToServer<DbRequestMessage<GetCharactersReq>, CharactersResp>(DatabaseRequestTypes.GetCharacters, GetCharacters);
            RegisterRequestToServer<DbRequestMessage<UpdateCharacterReq>, CharacterResp>(DatabaseRequestTypes.UpdateCharacter, UpdateCharacter);
            RegisterRequestToServer<DbRequestMessage<DeleteCharacterReq>, EmptyMessage>(DatabaseRequestTypes.DeleteCharacter, DeleteCharacter);
            RegisterRequestToServer<DbRequestMessage<FindCharacterNameReq>, FindCharacterNameResp>(DatabaseRequestTypes.FindCharacterName, FindCharacterName);
            RegisterRequestToServer<DbRequestMessage<FindCharacterNameReq>, SocialCharactersResp>(DatabaseRequestTypes.FindCharacters, FindCharacters);
            RegisterRequestToServer<DbRequestMessage<CreateFriendReq>, EmptyMessage>(DatabaseRequestTypes.CreateFriend, CreateFriend);
            RegisterRequestToServer<DbRequestMessage<DeleteFriendReq>, EmptyMessage>(DatabaseRequestTypes.DeleteFriend, DeleteFriend);
            RegisterRequestToServer<DbRequestMessage<GetFriendsReq>, SocialCharactersResp>(DatabaseRequestTypes.GetFriends, GetFriends);
            RegisterRequestToServer<DbRequestMessage<GetFriendRequestNotificationReq>, GetFriendRequestNotificationResp>(DatabaseRequestTypes.GetFriendRequestNotification, GetFriendRequestNotification);
            RegisterRequestToServer<DbRequestMessage<CreateBuildingReq>, BuildingResp>(DatabaseRequestTypes.CreateBuilding, CreateBuilding);
            RegisterRequestToServer<DbRequestMessage<UpdateBuildingReq>, BuildingResp>(DatabaseRequestTypes.UpdateBuilding, UpdateBuilding);
            RegisterRequestToServer<DbRequestMessage<DeleteBuildingReq>, EmptyMessage>(DatabaseRequestTypes.DeleteBuilding, DeleteBuilding);
            RegisterRequestToServer<DbRequestMessage<GetBuildingsReq>, BuildingsResp>(DatabaseRequestTypes.GetBuildings, GetBuildings);
            RegisterRequestToServer<DbRequestMessage<CreatePartyReq>, PartyResp>(DatabaseRequestTypes.CreateParty, CreateParty);
            RegisterRequestToServer<DbRequestMessage<UpdatePartyReq>, PartyResp>(DatabaseRequestTypes.UpdateParty, UpdateParty);
            RegisterRequestToServer<DbRequestMessage<UpdatePartyLeaderReq>, PartyResp>(DatabaseRequestTypes.UpdatePartyLeader, UpdatePartyLeader);
            RegisterRequestToServer<DbRequestMessage<DeletePartyReq>, EmptyMessage>(DatabaseRequestTypes.DeleteParty, DeleteParty);
            RegisterRequestToServer<DbRequestMessage<UpdateCharacterPartyReq>, PartyResp>(DatabaseRequestTypes.UpdateCharacterParty, UpdateCharacterParty);
            RegisterRequestToServer<DbRequestMessage<ClearCharacterPartyReq>, EmptyMessage>(DatabaseRequestTypes.ClearCharacterParty, ClearCharacterParty);
            RegisterRequestToServer<DbRequestMessage<GetPartyReq>, PartyResp>(DatabaseRequestTypes.GetParty, GetParty);
            RegisterRequestToServer<DbRequestMessage<CreateGuildReq>, GuildResp>(DatabaseRequestTypes.CreateGuild, CreateGuild);
            RegisterRequestToServer<DbRequestMessage<UpdateGuildLeaderReq>, GuildResp>(DatabaseRequestTypes.UpdateGuildLeader, UpdateGuildLeader);
            RegisterRequestToServer<DbRequestMessage<UpdateGuildMessageReq>, GuildResp>(DatabaseRequestTypes.UpdateGuildMessage, UpdateGuildMessage);
            RegisterRequestToServer<DbRequestMessage<UpdateGuildMessageReq>, GuildResp>(DatabaseRequestTypes.UpdateGuildMessage2, UpdateGuildMessage2);
            RegisterRequestToServer<DbRequestMessage<UpdateGuildScoreReq>, GuildResp>(DatabaseRequestTypes.UpdateGuildScore, UpdateGuildScore);
            RegisterRequestToServer<DbRequestMessage<UpdateGuildOptionsReq>, GuildResp>(DatabaseRequestTypes.UpdateGuildOptions, UpdateGuildOptions);
            RegisterRequestToServer<DbRequestMessage<UpdateGuildAutoAcceptRequestsReq>, GuildResp>(DatabaseRequestTypes.UpdateGuildAutoAcceptRequests, UpdateGuildAutoAcceptRequests);
            RegisterRequestToServer<DbRequestMessage<UpdateGuildRankReq>, GuildResp>(DatabaseRequestTypes.UpdateGuildRank, UpdateGuildRank);
            RegisterRequestToServer<DbRequestMessage<UpdateGuildRoleReq>, GuildResp>(DatabaseRequestTypes.UpdateGuildRole, UpdateGuildRole);
            RegisterRequestToServer<DbRequestMessage<UpdateGuildMemberRoleReq>, GuildResp>(DatabaseRequestTypes.UpdateGuildMemberRole, UpdateGuildMemberRole);
            RegisterRequestToServer<DbRequestMessage<DeleteGuildReq>, EmptyMessage>(DatabaseRequestTypes.DeleteGuild, DeleteGuild);
            RegisterRequestToServer<DbRequestMessage<UpdateCharacterGuildReq>, GuildResp>(DatabaseRequestTypes.UpdateCharacterGuild, UpdateCharacterGuild);
            RegisterRequestToServer<DbRequestMessage<ClearCharacterGuildReq>, EmptyMessage>(DatabaseRequestTypes.ClearCharacterGuild, ClearCharacterGuild);
            RegisterRequestToServer<DbRequestMessage<FindGuildNameReq>, FindGuildNameResp>(DatabaseRequestTypes.FindGuildName, FindGuildName);
            RegisterRequestToServer<DbRequestMessage<GetGuildReq>, GuildResp>(DatabaseRequestTypes.GetGuild, GetGuild);
            RegisterRequestToServer<DbRequestMessage<IncreaseGuildExpReq>, GuildResp>(DatabaseRequestTypes.IncreaseGuildExp, IncreaseGuildExp);
            RegisterRequestToServer<DbRequestMessage<AddGuildSkillReq>, GuildResp>(DatabaseRequestTypes.AddGuildSkill, AddGuildSkill);
            RegisterRequestToServer<DbRequestMessage<GetGuildGoldReq>, GuildGoldResp>(DatabaseRequestTypes.GetGuildGold, GetGuildGold);
            RegisterRequestToServer<DbRequestMessage<ChangeGuildGoldReq>, GuildGoldResp>(DatabaseRequestTypes.ChangeGuildGold, ChangeGuildGold);
            RegisterRequestToServer<DbRequestMessage<GetStorageItemsReq>, GetStorageItemsResp>(DatabaseRequestTypes.GetStorageItems, GetStorageItems);
            RegisterRequestToServer<DbRequestMessage<UpdateStorageItemsReq>, EmptyMessage>(DatabaseRequestTypes.UpdateStorageItems, UpdateStorageItems);
            RegisterRequestToServer<DbRequestMessage<UpdateStorageAndCharacterItemsReq>, EmptyMessage>(DatabaseRequestTypes.UpdateStorageAndCharacterItems, UpdateStorageAndCharacterItems);
            RegisterRequestToServer<DbRequestMessage<EmptyMessage>, EmptyMessage>(DatabaseRequestTypes.DeleteAllReservedStorage, DeleteAllReservedStorage);
            RegisterRequestToServer<DbRequestMessage<MailListReq>, MailListResp>(DatabaseRequestTypes.MailList, MailList);
            RegisterRequestToServer<DbRequestMessage<UpdateReadMailStateReq>, UpdateReadMailStateResp>(DatabaseRequestTypes.UpdateReadMailState, UpdateReadMailState);
            RegisterRequestToServer<DbRequestMessage<UpdateClaimMailItemsStateReq>, UpdateClaimMailItemsStateResp>(DatabaseRequestTypes.UpdateClaimMailItemsState, UpdateClaimMailItemsState);
            RegisterRequestToServer<DbRequestMessage<UpdateDeleteMailStateReq>, UpdateDeleteMailStateResp>(DatabaseRequestTypes.UpdateDeleteMailState, UpdateDeleteMailState);
            RegisterRequestToServer<DbRequestMessage<SendMailReq>, SendMailResp>(DatabaseRequestTypes.SendMail, SendMail);
            RegisterRequestToServer<DbRequestMessage<GetMailReq>, GetMailResp>(DatabaseRequestTypes.GetMail, GetMail);
            RegisterRequestToServer<DbRequestMessage<GetMailNotificationReq>, GetMailNotificationResp>(DatabaseRequestTypes.GetMailNotification, GetMailNotification);
            RegisterRequestToServer<DbRequestMessage<GetIdByCharacterNameReq>, GetIdByCharacterNameResp>(DatabaseRequestTypes.GetIdByCharacterName, GetIdByCharacterName);
            RegisterRequestToServer<DbRequestMessage<GetUserIdByCharacterNameReq>, GetUserIdByCharacterNameResp>(DatabaseRequestTypes.GetUserIdByCharacterName, GetUserIdByCharacterName);
            RegisterRequestToServer<DbRequestMessage<GetUserUnbanTimeReq>, GetUserUnbanTimeResp>(DatabaseRequestTypes.GetUserUnbanTime, GetUserUnbanTime);
            RegisterRequestToServer<DbRequestMessage<SetUserUnbanTimeByCharacterNameReq>, EmptyMessage>(DatabaseRequestTypes.SetUserUnbanTimeByCharacterName, SetUserUnbanTimeByCharacterName);
            RegisterRequestToServer<DbRequestMessage<SetCharacterUnmuteTimeByNameReq>, EmptyMessage>(DatabaseRequestTypes.SetCharacterUnmuteTimeByName, SetCharacterUnmuteTimeByName);
            RegisterRequestToServer<DbRequestMessage<GetSummonBuffsReq>, GetSummonBuffsResp>(DatabaseRequestTypes.GetSummonBuffs, GetSummonBuffs);
            RegisterRequestToServer<DbRequestMessage<FindEmailReq>, FindEmailResp>(DatabaseRequestTypes.FindEmail, FindEmail);
            RegisterRequestToServer<DbRequestMessage<ValidateEmailVerificationReq>, ValidateEmailVerificationResp>(DatabaseRequestTypes.ValidateEmailVerification, ValidateEmailVerification);
            RegisterRequestToServer<DbRequestMessage<UpdateUserCountReq>, EmptyMessage>(DatabaseRequestTypes.UpdateUserCount, UpdateUserCount);
            RegisterRequestToServer<DbRequestMessage<GetSocialCharacterReq>, SocialCharacterResp>(DatabaseRequestTypes.GetSocialCharacter, GetSocialCharacter);
            RegisterRequestToServer<DbRequestMessage<FindGuildNameReq>, GuildsResp>(DatabaseRequestTypes.FindGuilds, FindGuilds);
            RegisterRequestToServer<DbRequestMessage<CreateGuildRequestReq>, EmptyMessage>(DatabaseRequestTypes.CreateGuildRequest, CreateGuildRequest);
            RegisterRequestToServer<DbRequestMessage<DeleteGuildRequestReq>, EmptyMessage>(DatabaseRequestTypes.DeleteGuildRequest, DeleteGuildRequest);
            RegisterRequestToServer<DbRequestMessage<GetGuildRequestsReq>, SocialCharactersResp>(DatabaseRequestTypes.GetGuildRequests, GetGuildRequests);
            RegisterRequestToServer<DbRequestMessage<GetGuildRequestNotificationReq>, GetGuildRequestNotificationResp>(DatabaseRequestTypes.GetGuildRequestNotification, GetGuildRequestNotification);
            RegisterRequestToServer<DbRequestMessage<UpdateGuildMemberCountReq>, EmptyMessage>(DatabaseRequestTypes.UpdateGuildMemberCount, UpdateGuildMemberCount);
            this.InvokeInstanceDevExtMethods("RegisterMessages");
        }
    }
}