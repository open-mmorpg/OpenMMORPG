#if (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Net.Sockets;
using LiteNetLib;
using LiteNetLib.Utils;
using LiteNetLibManager;
using MultiplayerARPG;
using MultiplayerARPG.MMO;
using NUnit.Framework;

namespace OpenMMORPG.Tests
{
    /// <summary>
    /// The login server's request handlers, called directly on a never-started LoginNetworkManager
    /// with a <see cref="TestDatabaseClient"/>. They decide who is logged in on which connection,
    /// which is what stops two people playing one account, and they must answer every request
    /// (a login that never gets a reply leaves the player stuck on the login screen). Handlers
    /// that need the cluster's answer (a first login asks whether the user is still on a map
    /// server) are not reachable this way.
    /// </summary>
    [Category(TestCategories.Harnessed)]
    public class LoginNetworkManagerTests : KitTestFixture
    {
        private TestDatabaseClient _db;
        private LoginNetworkManager _login;

        /// <summary>Accepts every class and model, so these tests need no game data.</summary>
        private class AcceptingDataManager : ILoginServerDataManager
        {
            public string GenerateCharacterId() => GenericUtils.GetUniqueId();

            public bool CanCreateCharacter(ref int dataId, ref int entityId, ref int factionId, IList<CharacterDataBoolean> publicBools, IList<CharacterDataInt32> publicInts, IList<CharacterDataFloat32> publicFloats, out UITextKeys errorMessage)
            {
                errorMessage = UITextKeys.NONE;
                return true;
            }

            public void SetNewPlayerCharacterData(PlayerCharacterData playerCharacterData, string characterName, int dataId, int entityId, int factionId, IList<CharacterDataBoolean> publicBools, IList<CharacterDataInt32> publicInts, IList<CharacterDataFloat32> publicFloats)
            {
                playerCharacterData.CharacterName = characterName;
                playerCharacterData.DataId = dataId;
                playerCharacterData.EntityId = entityId;
                playerCharacterData.FactionId = factionId;
            }

            public string GenerateAccessToken(string userId) => userId + "-token";

            public string GetUserIdFromAccessToken(string accessToken) => accessToken.Replace("-token", string.Empty);
        }

        [SetUp]
        public void SetUp()
        {
            _db = TestDatabaseClient.Install(Objects);
            // Awake and Start never run in edit mode, so no transport or socket is created.
            _login = Objects.NewGameObject(nameof(LoginNetworkManagerTests)).AddComponent<LoginNetworkManager>();
            _login.DataManager = new AcceptingDataManager();
            // Kicks are sent through the cluster client; unconnected, it drops them.
            TestReflection.SetProperty(_login, nameof(LoginNetworkManager.ClusterClient), new ClusterClient(_login));
        }

        private ConcurrentDictionary<long, CentralUserPeerInfo> PeersByConnection => TestReflection.GetField<ConcurrentDictionary<long, CentralUserPeerInfo>>(_login, "_userPeers");
        private ConcurrentDictionary<string, CentralUserPeerInfo> PeersByUser => TestReflection.GetField<ConcurrentDictionary<string, CentralUserPeerInfo>>(_login, "_userPeersByUserId");

        private struct Reply<T>
        {
            public AckResponseCode Code;
            public T Response;
        }

        /// <summary>Calls a request handler as connection <paramref name="connectionId"/> and returns its replies.</summary>
        private List<Reply<TResponse>> Request<TRequest, TResponse>(string handler, long connectionId, TRequest request)
            where TResponse : INetSerializable
        {
            List<Reply<TResponse>> replies = new List<Reply<TResponse>>();
            RequestProceedResultDelegate<TResponse> result = (code, response, extra) => replies.Add(new Reply<TResponse>() { Code = code, Response = response });
            RequestHandlerData data = new RequestHandlerData(0, 0, null, connectionId, new NetDataReader(new byte[0]));
            TestReflection.Invoke(_login, handler, data, request, result);
            return replies;
        }

        private static Reply<T> Single<T>(List<Reply<T>> replies)
        {
            Assert.That(replies, Has.Count.EqualTo(1), "the request is answered exactly once");
            return replies[0];
        }

        private List<Reply<ResponseUserLoginMessage>> Login(long connectionId, string username, string password = "secret")
        {
            return Request<RequestUserLoginMessage, ResponseUserLoginMessage>("HandleRequestUserLogin", connectionId, new RequestUserLoginMessage() { username = username, password = password });
        }

        private Reply<ResponseUserRegisterMessage> Register(string username, string password = "secret", string email = "")
        {
            return Single(Request<RequestUserRegisterMessage, ResponseUserRegisterMessage>("HandleRequestUserRegister", 1, new RequestUserRegisterMessage() { username = username, password = password, email = email }));
        }

        private Reply<ResponseCreateCharacterMessage> CreateCharacter(long connectionId, string characterName)
        {
            return Single(Request<RequestCreateCharacterMessage, ResponseCreateCharacterMessage>("HandleRequestCreateCharacter", connectionId, new RequestCreateCharacterMessage() { characterName = characterName }));
        }

        private void TokensPass(bool isPass, long unbanTime = 0)
        {
            _db.On<ValidateAccessTokenReq, ValidateAccessTokenResp>(nameof(IDatabaseClient.ValidateAccessTokenAsync), req => new ValidateAccessTokenResp() { IsPass = isPass });
            _db.On<GetUserUnbanTimeReq, GetUserUnbanTimeResp>(nameof(IDatabaseClient.GetUserUnbanTimeAsync), req => new GetUserUnbanTimeResp() { UnbanTime = unbanTime });
        }

        private Reply<ResponseValidateAccessTokenMessage> ShowToken(long connectionId, string userId)
        {
            return Single(Request<RequestValidateAccessTokenMessage, ResponseValidateAccessTokenMessage>("HandleRequestValidateAccessToken", connectionId, new RequestValidateAccessTokenMessage() { userId = userId, accessToken = userId + "-token" }));
        }

        /// <summary>Logs a user in on a connection the way a returning client does, with its access token.</summary>
        private void SignIn(long connectionId, string userId)
        {
            TokensPass(true);
            Assert.That(ShowToken(connectionId, userId).Code, Is.EqualTo(AckResponseCode.Success));
        }

        private void PasswordsBelongTo(string userId)
        {
            _db.On<ValidateUserLoginReq, ValidateUserLoginResp>(nameof(IDatabaseClient.ValidateUserLoginAsync), req => new ValidateUserLoginResp() { UserId = userId });
        }

        /// <summary>Every account already has this many characters.</summary>
        private void AccountsHaveCharacters(int count)
        {
            _db.On<GetCharactersReq, CharactersResp>(nameof(IDatabaseClient.GetCharactersAsync), req => new CharactersResp() { List = Enumerable.Range(0, count).Select(i => new PlayerCharacterData()).ToList() });
        }

        private int CallsTo(string method)
        {
            return _db.Calls.Count(c => c == method);
        }

        /// <summary>Gives the login server's flood limits a clock the test moves. Returns the function that moves it, in seconds.</summary>
        private System.Action<double> UseFloodClock()
        {
            long now = 5000000;
            _login.FloodGuard = new LoginFloodGuard(_login.floodProtection, () => now);
            return seconds => now += (long)System.Math.Round(seconds * 1000);
        }

        // Login

        [Test]
        public void WithDefaultLoginDisabledLoginAndRegistrationAreRefused()
        {
            _login.disableDefaultLogin = true;

            Assert.That(Single(Login(1, "alice")).Response.message, Is.EqualTo(UITextKeys.UI_ERROR_SERVICE_NOT_AVAILABLE));
            Assert.That(Register("alice").Response.message, Is.EqualTo(UITextKeys.UI_ERROR_SERVICE_NOT_AVAILABLE));
            Assert.That(_db.Calls, Is.Empty);
        }

        [Test]
        public void AWrongPasswordLogsNoOneIn()
        {
            PasswordsBelongTo(string.Empty);

            Reply<ResponseUserLoginMessage> reply = Single(Login(1, "alice", "wrong"));

            Assert.That(reply.Code, Is.EqualTo(AckResponseCode.Error));
            Assert.That(reply.Response.message, Is.EqualTo(UITextKeys.UI_ERROR_INVALID_USERNAME_OR_PASSWORD));
            Assert.That(reply.Response.accessToken, Is.Null.Or.Empty);
            Assert.That(PeersByConnection, Is.Empty);
            Assert.That(_db.WasCalled(nameof(IDatabaseClient.UpdateAccessTokenAsync)), Is.False);
        }

        [Test]
        public void ALoginToAnAccountThatIsAlreadyLoggedInIsRefusedAndEndsTheOtherSession()
        {
            SignIn(1, "alice");
            PasswordsBelongTo("alice");

            Reply<ResponseUserLoginMessage> reply = Single(Login(2, "alice"));

            Assert.That(reply.Code, Is.EqualTo(AckResponseCode.Error));
            Assert.That(reply.Response.message, Is.EqualTo(UITextKeys.UI_ERROR_ALREADY_LOGGED_IN));
            Assert.That(PeersByConnection.ContainsKey(1), Is.False, "the first session no longer acts as alice");
            Assert.That(PeersByConnection.ContainsKey(2), Is.False, "the second is not let in either");
            Assert.That(PeersByUser.ContainsKey("alice"), Is.False);
            Assert.That(_db.WasCalled(nameof(IDatabaseClient.UpdateAccessTokenAsync)), Is.False, "no new token is issued");
        }

        [Test]
        public void LoginsBeyondTheLimitWaitAndAreAnsweredInArrivalOrder()
        {
            List<string> asked = new List<string>();
            _db.On<ValidateUserLoginReq, ValidateUserLoginResp>(nameof(IDatabaseClient.ValidateUserLoginAsync), req =>
            {
                asked.Add(req.Username);
                return new ValidateUserLoginResp() { UserId = string.Empty };
            });
            _login.MaxConcurrentRequest = 1;
            // One login is already being checked.
            TestReflection.SetField(_login, "activeLogins", 1);

            List<Reply<ResponseUserLoginMessage>> alice = Login(1, "alice");
            List<Reply<ResponseUserLoginMessage>> bob = Login(2, "bob");

            Assert.That(alice, Is.Empty, "alice waits");
            Assert.That(bob, Is.Empty, "bob waits");
            Assert.That(asked, Is.Empty);

            // The running login finishes.
            TestReflection.SetField(_login, "activeLogins", 0);
            TestReflection.Invoke(_login, "TryDequeue");

            Assert.That(asked, Is.EqualTo(new[] { "alice", "bob" }));
            Assert.That(Single(alice).Response.message, Is.EqualTo(UITextKeys.UI_ERROR_INVALID_USERNAME_OR_PASSWORD));
            Assert.That(Single(bob).Response.message, Is.EqualTo(UITextKeys.UI_ERROR_INVALID_USERNAME_OR_PASSWORD));
            Assert.That(TestReflection.GetField<int>(_login, "activeLogins"), Is.Zero, "every finished login is counted out");
        }

        // Access tokens and sessions

        [Test]
        public void AnAccessTokenShownOnANewConnectionMovesTheSessionThere()
        {
            SignIn(1, "alice");
            SignIn(2, "alice");

            Assert.That(PeersByConnection.ContainsKey(1), Is.False, "the old connection no longer acts as alice");
            Assert.That(PeersByConnection[2].userId, Is.EqualTo("alice"));
            Assert.That(PeersByUser["alice"].connectionId, Is.EqualTo(2));
        }

        [Test]
        public void AnInvalidTokenOrABannedAccountIsNotLoggedIn()
        {
            TokensPass(false);
            Assert.That(ShowToken(1, "alice").Response.message, Is.EqualTo(UITextKeys.UI_ERROR_INVALID_USER_TOKEN));

            TokensPass(true, System.DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600);
            Assert.That(ShowToken(1, "alice").Response.message, Is.EqualTo(UITextKeys.UI_ERROR_USER_BANNED));

            Assert.That(PeersByConnection, Is.Empty);
            Assert.That(PeersByUser, Is.Empty);
        }

        [Test]
        public void DisconnectingOrLoggingOutEndsTheSession()
        {
            _db.Succeed(nameof(IDatabaseClient.UpdateAccessTokenAsync));
            SignIn(1, "alice");
            SignIn(2, "bob");

            _login.OnPeerDisconnected(1, DisconnectReason.RemoteConnectionClose, SocketError.Success);
            Reply<EmptyMessage> logout = Single(Request<EmptyMessage, EmptyMessage>("HandleRequestUserLogout", 2, EmptyMessage.Value));

            Assert.That(logout.Code, Is.EqualTo(AckResponseCode.Success));
            Assert.That(PeersByConnection, Is.Empty);
            Assert.That(PeersByUser, Is.Empty, "neither account stays 'already logged in'");
            UpdateAccessTokenReq cleared = _db.LastRequest<UpdateAccessTokenReq>(nameof(IDatabaseClient.UpdateAccessTokenAsync));
            Assert.That(cleared.UserId, Is.EqualTo("bob"));
            Assert.That(cleared.AccessToken, Is.Empty, "logging out revokes the token");
        }

        // Registration

        [Test]
        public void AnInvalidUsernameOrEmailIsRefusedBeforeTheDatabaseIsAsked()
        {
            _login.requireEmail = true;

            Assert.That(Register("alice smith", email: "a@b.com").Response.message, Is.EqualTo(UITextKeys.UI_ERROR_INVALID_USERNAME));
            Assert.That(Register("alice", email: "not-an-email").Response.message, Is.EqualTo(UITextKeys.UI_ERROR_INVALID_EMAIL));
            Assert.That(Register("alice", email: "").Response.message, Is.EqualTo(UITextKeys.UI_ERROR_INVALID_EMAIL));
            Assert.That(_db.Calls, Is.Empty);
        }

        [Test]
        public void ATakenUsernameOrEmailIsRefusedAndNothingIsCreated()
        {
            _login.requireEmail = true;
            _db.On<FindEmailReq, FindEmailResp>(nameof(IDatabaseClient.FindEmailAsync), req => new FindEmailResp() { FoundAmount = req.Email == "taken@b.com" ? 1 : 0 });
            _db.On<FindUsernameReq, FindUsernameResp>(nameof(IDatabaseClient.FindUsernameAsync), req => new FindUsernameResp() { FoundAmount = req.Username == "alice" ? 1 : 0 });

            Assert.That(Register("bob", email: "taken@b.com").Response.message, Is.EqualTo(UITextKeys.UI_ERROR_EMAIL_ALREADY_IN_USE));
            Assert.That(Register("alice", email: "free@b.com").Response.message, Is.EqualTo(UITextKeys.UI_ERROR_USERNAME_EXISTED));
            Assert.That(_db.WasCalled(nameof(IDatabaseClient.CreateUserLoginAsync)), Is.False);
        }

        [Test]
        public void UsernamesAndPasswordsOutsideTheLengthLimitsAreRefused()
        {
            _db.On<FindUsernameReq, FindUsernameResp>(nameof(IDatabaseClient.FindUsernameAsync), req => new FindUsernameResp() { FoundAmount = 0 });

            Assert.That(Register(new string('a', _login.minUsernameLength - 1)).Response.message, Is.EqualTo(UITextKeys.UI_ERROR_USERNAME_TOO_SHORT));
            Assert.That(Register(new string('a', _login.maxUsernameLength + 1)).Response.message, Is.EqualTo(UITextKeys.UI_ERROR_USERNAME_TOO_LONG));
            Assert.That(Register("alice", new string('p', _login.minPasswordLength - 1)).Response.message, Is.EqualTo(UITextKeys.UI_ERROR_PASSWORD_TOO_SHORT));
            Assert.That(Register("alice", "  " + new string('p', _login.minPasswordLength - 1) + "  ").Response.message, Is.EqualTo(UITextKeys.UI_ERROR_PASSWORD_TOO_SHORT), "spaces do not count");
            Assert.That(_db.WasCalled(nameof(IDatabaseClient.CreateUserLoginAsync)), Is.False);
        }

        [Test]
        public void AGoodRegistrationCreatesTheTrimmedLogin()
        {
            _db.On<FindUsernameReq, FindUsernameResp>(nameof(IDatabaseClient.FindUsernameAsync), req => new FindUsernameResp() { FoundAmount = 0 });
            _db.Succeed(nameof(IDatabaseClient.CreateUserLoginAsync));

            Reply<ResponseUserRegisterMessage> reply = Register(" alice ", " secret ", " a@b.com ");

            Assert.That(reply.Code, Is.EqualTo(AckResponseCode.Success));
            CreateUserLoginReq created = _db.LastRequest<CreateUserLoginReq>(nameof(IDatabaseClient.CreateUserLoginAsync));
            Assert.That(created.Username, Is.EqualTo("alice"));
            Assert.That(created.Password, Is.EqualTo("secret"));
            Assert.That(created.Email, Is.EqualTo("a@b.com"));
        }

        // Characters

        [Test]
        public void ACharacterNameThatBreaksTheRulesIsRefusedBeforeTheDatabaseIsAsked()
        {
            SignIn(1, "alice");

            Assert.That(CreateCharacter(1, "Ro wan").Response.message, Is.EqualTo(UITextKeys.UI_ERROR_INVALID_CHARACTER_NAME));
            Assert.That(CreateCharacter(1, new string('a', _login.minCharacterNameLength - 1)).Response.message, Is.EqualTo(UITextKeys.UI_ERROR_CHARACTER_NAME_TOO_SHORT));
            Assert.That(CreateCharacter(1, new string('a', _login.maxCharacterNameLength + 1)).Response.message, Is.EqualTo(UITextKeys.UI_ERROR_CHARACTER_NAME_TOO_LONG));
            Assert.That(_db.WasCalled(nameof(IDatabaseClient.FindCharacterNameAsync)), Is.False);
            Assert.That(_db.WasCalled(nameof(IDatabaseClient.CreateCharacterAsync)), Is.False);
        }

        [Test]
        public void ATakenNameOrAConnectionThatIsNotLoggedInCreatesNoCharacter()
        {
            _db.On<FindCharacterNameReq, FindCharacterNameResp>(nameof(IDatabaseClient.FindCharacterNameAsync), req => new FindCharacterNameResp() { FoundAmount = req.CharacterName == "Taken" ? 1 : 0 });
            AccountsHaveCharacters(0);
            SignIn(1, "alice");

            Assert.That(CreateCharacter(1, "Taken").Response.message, Is.EqualTo(UITextKeys.UI_ERROR_CHARACTER_NAME_EXISTED));
            Assert.That(CreateCharacter(2, "Rowan").Response.message, Is.EqualTo(UITextKeys.UI_ERROR_NOT_LOGGED_IN));
            Assert.That(_db.WasCalled(nameof(IDatabaseClient.CreateCharacterAsync)), Is.False);
        }

        [Test]
        public void ANewCharacterBelongsToTheLoggedInAccount()
        {
            _db.On<FindCharacterNameReq, FindCharacterNameResp>(nameof(IDatabaseClient.FindCharacterNameAsync), req => new FindCharacterNameResp() { FoundAmount = 0 });
            _db.On<CreateCharacterReq, CharacterResp>(nameof(IDatabaseClient.CreateCharacterAsync), req => new CharacterResp() { CharacterData = req.CharacterData });
            AccountsHaveCharacters(0);
            SignIn(1, "alice");
            SignIn(2, "bob");

            Assert.That(CreateCharacter(2, "  Rowan  ").Code, Is.EqualTo(AckResponseCode.Success));

            CreateCharacterReq created = _db.LastRequest<CreateCharacterReq>(nameof(IDatabaseClient.CreateCharacterAsync));
            Assert.That(created.UserId, Is.EqualTo("bob"));
            Assert.That(created.CharacterData.CharacterName, Is.EqualTo("Rowan"));
            Assert.That(created.CharacterData.Id, Is.Not.Empty);
        }

        [Test]
        public void ListingDeletingOrSelectingCharactersNeedsALogin()
        {
            Assert.That(Single(Request<EmptyMessage, ResponseCharactersMessage>("HandleRequestCharacters", 1, EmptyMessage.Value)).Response.message, Is.EqualTo(UITextKeys.UI_ERROR_NOT_LOGGED_IN));
            Assert.That(Single(Request<RequestDeleteCharacterMessage, ResponseDeleteCharacterMessage>("HandleRequestDeleteCharacter", 1, new RequestDeleteCharacterMessage() { characterId = "c1" })).Response.message, Is.EqualTo(UITextKeys.UI_ERROR_NOT_LOGGED_IN));
            Assert.That(Single(Request<RequestSelectCharacterMessage, ResponseSelectCharacterMessage>("HandleRequestSelectCharacter", 1, new RequestSelectCharacterMessage() { characterId = "c1" })).Response.message, Is.EqualTo(UITextKeys.UI_ERROR_NOT_LOGGED_IN));
            Assert.That(_db.Calls, Is.Empty);
        }

        [Test]
        public void AnAccountListsAndDeletesOnlyItsOwnCharacters()
        {
            _db.On<GetCharactersReq, CharactersResp>(nameof(IDatabaseClient.GetCharactersAsync), req => new CharactersResp() { List = new List<PlayerCharacterData>() });
            _db.Succeed(nameof(IDatabaseClient.DeleteCharacterAsync));
            SignIn(1, "alice");
            SignIn(2, "bob");

            Single(Request<EmptyMessage, ResponseCharactersMessage>("HandleRequestCharacters", 2, EmptyMessage.Value));
            Reply<ResponseDeleteCharacterMessage> deleted = Single(Request<RequestDeleteCharacterMessage, ResponseDeleteCharacterMessage>("HandleRequestDeleteCharacter", 2, new RequestDeleteCharacterMessage() { characterId = "alices-character" }));

            Assert.That(_db.LastRequest<GetCharactersReq>(nameof(IDatabaseClient.GetCharactersAsync)).UserId, Is.EqualTo("bob"));
            Assert.That(deleted.Code, Is.EqualTo(AckResponseCode.Success));
            DeleteCharacterReq delete = _db.LastRequest<DeleteCharacterReq>(nameof(IDatabaseClient.DeleteCharacterAsync));
            Assert.That(delete.UserId, Is.EqualTo("bob"), "the database is asked for bob's character, so alice's is not deleted");
            Assert.That(delete.CharacterId, Is.EqualTo("alices-character"));
        }

        // Flood protection. The limits themselves are tested in LoginFloodGuardTests; these check
        // that the handlers ask them, in time, and answer every request they refuse.

        [Test]
        public void ANormalFirstSessionPassesTheDefaultLimitsFirstTime()
        {
            _db.On<FindUsernameReq, FindUsernameResp>(nameof(IDatabaseClient.FindUsernameAsync), req => new FindUsernameResp() { FoundAmount = 0 });
            _db.Succeed(nameof(IDatabaseClient.CreateUserLoginAsync));
            _db.On<FindCharacterNameReq, FindCharacterNameResp>(nameof(IDatabaseClient.FindCharacterNameAsync), req => new FindCharacterNameResp() { FoundAmount = 0 });
            _db.On<CreateCharacterReq, CharacterResp>(nameof(IDatabaseClient.CreateCharacterAsync), req => new CharacterResp() { CharacterData = req.CharacterData });
            AccountsHaveCharacters(0);

            // Register
            Assert.That(Register("alice").Code, Is.EqualTo(AckResponseCode.Success));
            // A mistyped password
            PasswordsBelongTo(string.Empty);
            Assert.That(Single(Login(1, "alice", "wrnog")).Response.message, Is.EqualTo(UITextKeys.UI_ERROR_INVALID_USERNAME_OR_PASSWORD));
            // The right one. Alice is made to look logged in elsewhere, the one login outcome that
            // needs no cluster: the password was checked and the login got past every limit.
            SignIn(9, "alice");
            PasswordsBelongTo("alice");
            Assert.That(Single(Login(1, "alice")).Response.message, Is.EqualTo(UITextKeys.UI_ERROR_ALREADY_LOGGED_IN));
            // Logged in, then a character
            SignIn(1, "alice");
            Assert.That(CreateCharacter(1, "Rowan").Code, Is.EqualTo(AckResponseCode.Success));
        }

        [Test]
        public void RegistrationsBeyondTheAddressLimitAreRefusedBeforeTheDatabaseIsAsked()
        {
            _db.On<FindUsernameReq, FindUsernameResp>(nameof(IDatabaseClient.FindUsernameAsync), req => new FindUsernameResp() { FoundAmount = 0 });
            _db.Succeed(nameof(IDatabaseClient.CreateUserLoginAsync));
            _login.floodProtection.registerMaxAccountsPerAddress = 2;
            System.Action<double> wait = UseFloodClock();

            Assert.That(Register("alice").Code, Is.EqualTo(AckResponseCode.Success));
            // A request that creates nothing does not use up an account slot
            Assert.That(Register("b").Response.message, Is.EqualTo(UITextKeys.UI_ERROR_USERNAME_TOO_SHORT));
            Assert.That(Register("bob").Code, Is.EqualTo(AckResponseCode.Success));
            int asked = _db.Calls.Count();

            Reply<ResponseUserRegisterMessage> third = Register("carol");

            Assert.That(third.Code, Is.EqualTo(AckResponseCode.Error));
            Assert.That(third.Response.message, Is.EqualTo(UITextKeys.UI_ERROR_TOO_MANY_REQUESTS));
            Assert.That(_db.Calls.Count(), Is.EqualTo(asked), "the database is not asked");
            Assert.That(CallsTo(nameof(IDatabaseClient.CreateUserLoginAsync)), Is.EqualTo(2));

            wait(3600);
            Assert.That(Register("carol").Code, Is.EqualTo(AckResponseCode.Success), "an hour later there is room again");
        }

        [Test]
        public void RegisterRequestsBeyondTheAttemptLimitAreRefusedBeforeTheDatabaseIsAsked()
        {
            _db.On<FindUsernameReq, FindUsernameResp>(nameof(IDatabaseClient.FindUsernameAsync), req => new FindUsernameResp() { FoundAmount = 1 });
            _login.floodProtection.registerMaxAttemptsPerAddress = 3;

            for (int i = 0; i < 3; ++i)
                Assert.That(Register("alice" + i).Response.message, Is.EqualTo(UITextKeys.UI_ERROR_USERNAME_EXISTED));
            Assert.That(Register("alice3").Response.message, Is.EqualTo(UITextKeys.UI_ERROR_TOO_MANY_REQUESTS), "the name probing stops");

            Assert.That(CallsTo(nameof(IDatabaseClient.FindUsernameAsync)), Is.EqualTo(3));
        }

        [Test]
        public void WrongPasswordsBeyondTheFreeOnesWaitWithoutTheDatabaseBeingAsked()
        {
            PasswordsBelongTo(string.Empty);
            _login.floodProtection.loginFailuresPerAddress = 2;
            System.Action<double> wait = UseFloodClock();

            // Two free, the third starts the wait
            for (int i = 0; i < 3; ++i)
                Assert.That(Single(Login(1, "user" + i, "wrong")).Response.message, Is.EqualTo(UITextKeys.UI_ERROR_INVALID_USERNAME_OR_PASSWORD));

            Reply<ResponseUserLoginMessage> refused = Single(Login(1, "user3", "wrong"));
            Assert.That(refused.Code, Is.EqualTo(AckResponseCode.Error));
            Assert.That(refused.Response.message, Is.EqualTo(UITextKeys.UI_ERROR_TOO_MANY_REQUESTS));
            Assert.That(CallsTo(nameof(IDatabaseClient.ValidateUserLoginAsync)), Is.EqualTo(3), "the fourth password is never checked");
            Assert.That(Single(Login(2, "user3", "wrong")).Response.message, Is.EqualTo(UITextKeys.UI_ERROR_INVALID_USERNAME_OR_PASSWORD), "another address is not affected");

            wait(30);
            Assert.That(Single(Login(1, "user3", "wrong")).Response.message, Is.EqualTo(UITextKeys.UI_ERROR_INVALID_USERNAME_OR_PASSWORD), "after the wait the next one is checked");
            Assert.That(Single(Login(1, "user4", "wrong")).Response.message, Is.EqualTo(UITextKeys.UI_ERROR_TOO_MANY_REQUESTS), "and, being wrong again, starts a longer one");
            wait(30);
            Assert.That(Single(Login(1, "user4", "wrong")).Response.message, Is.EqualTo(UITextKeys.UI_ERROR_TOO_MANY_REQUESTS), "twice as long");
        }

        [Test]
        public void WrongPasswordsForOneAccountMakeItWaitButARightOneClearsTheCount()
        {
            _login.floodProtection.loginFailuresPerUsername = 2;
            SignIn(9, "alice");

            // Each from another connection, so no address is over its own limit
            PasswordsBelongTo(string.Empty);
            Login(1, "alice", "wrong");
            Login(2, "alice", "wrong");
            // The right password, before the limit is passed
            PasswordsBelongTo("alice");
            Assert.That(Single(Login(3, "alice")).Response.message, Is.EqualTo(UITextKeys.UI_ERROR_ALREADY_LOGGED_IN), "the password was accepted");
            // The count started over: two more are free, the third starts the wait
            PasswordsBelongTo(string.Empty);
            Login(4, "alice", "wrong");
            Login(5, "alice", "wrong");
            Assert.That(Single(Login(6, "alice", "wrong")).Response.message, Is.EqualTo(UITextKeys.UI_ERROR_INVALID_USERNAME_OR_PASSWORD));
            int checkedPasswords = CallsTo(nameof(IDatabaseClient.ValidateUserLoginAsync));

            PasswordsBelongTo("alice");
            Assert.That(Single(Login(7, "Alice")).Response.message, Is.EqualTo(UITextKeys.UI_ERROR_TOO_MANY_REQUESTS), "the account waits, whoever asks");
            Assert.That(CallsTo(nameof(IDatabaseClient.ValidateUserLoginAsync)), Is.EqualTo(checkedPasswords));
        }

        [Test]
        public void AFullLoginQueueRefusesAtOnce()
        {
            PasswordsBelongTo(string.Empty);
            _login.MaxConcurrentRequest = 1;
            _login.floodProtection.loginMaxQueue = 2;
            TestReflection.SetField(_login, "activeLogins", 1);

            List<Reply<ResponseUserLoginMessage>> alice = Login(1, "alice");
            List<Reply<ResponseUserLoginMessage>> bob = Login(2, "bob");
            List<Reply<ResponseUserLoginMessage>> carol = Login(3, "carol");

            Assert.That(alice, Is.Empty, "alice waits");
            Assert.That(bob, Is.Empty, "bob waits");
            Assert.That(Single(carol).Response.message, Is.EqualTo(UITextKeys.UI_ERROR_SERVICE_NOT_AVAILABLE), "there is no room for carol, and she is told so");
            Assert.That(_db.Calls, Is.Empty);

            // Carol's refusal left nothing behind: once there is room she may try again
            TestReflection.SetField(_login, "activeLogins", 0);
            TestReflection.Invoke(_login, "TryDequeue");
            Assert.That(Single(alice).Response.message, Is.EqualTo(UITextKeys.UI_ERROR_INVALID_USERNAME_OR_PASSWORD));
            Assert.That(Single(bob).Response.message, Is.EqualTo(UITextKeys.UI_ERROR_INVALID_USERNAME_OR_PASSWORD));
            Assert.That(Single(Login(3, "carol")).Response.message, Is.EqualTo(UITextKeys.UI_ERROR_INVALID_USERNAME_OR_PASSWORD));
        }

        [Test]
        public void AConnectionCannotHoldMoreThanOnePlaceInTheLoginQueue()
        {
            PasswordsBelongTo(string.Empty);
            _login.MaxConcurrentRequest = 1;
            TestReflection.SetField(_login, "activeLogins", 1);

            List<Reply<ResponseUserLoginMessage>> first = Login(1, "alice");
            Reply<ResponseUserLoginMessage> second = Single(Login(1, "bob"));

            Assert.That(first, Is.Empty, "the first waits");
            Assert.That(second.Response.message, Is.EqualTo(UITextKeys.UI_ERROR_TOO_MANY_REQUESTS));
            Assert.That(TestReflection.GetField<Queue<LoginQueueEntry>>(_login, "loginQueue"), Has.Count.EqualTo(1));

            TestReflection.SetField(_login, "activeLogins", 0);
            TestReflection.Invoke(_login, "TryDequeue");
            Assert.That(Single(first).Response.message, Is.EqualTo(UITextKeys.UI_ERROR_INVALID_USERNAME_OR_PASSWORD));
            Assert.That(Single(Login(1, "bob")).Response.message, Is.EqualTo(UITextKeys.UI_ERROR_INVALID_USERNAME_OR_PASSWORD), "once it is answered the connection may log in again");
        }

        [Test]
        public void AQueuedLoginThatLostItsConnectionOrWaitedTooLongIsNotChecked()
        {
            List<string> asked = new List<string>();
            _db.On<ValidateUserLoginReq, ValidateUserLoginResp>(nameof(IDatabaseClient.ValidateUserLoginAsync), req =>
            {
                asked.Add(req.Username);
                return new ValidateUserLoginResp() { UserId = string.Empty };
            });
            System.Action<double> wait = UseFloodClock();
            _login.MaxConcurrentRequest = 1;
            TestReflection.SetField(_login, "activeLogins", 1);

            List<Reply<ResponseUserLoginMessage>> alice = Login(1, "alice");
            List<Reply<ResponseUserLoginMessage>> bob = Login(2, "bob");
            // Alice gives up and closes the game; 26 seconds pass before carol joins the queue
            _login.OnPeerDisconnected(1, DisconnectReason.RemoteConnectionClose, SocketError.Success);
            wait(26);
            List<Reply<ResponseUserLoginMessage>> carol = Login(3, "carol");

            TestReflection.SetField(_login, "activeLogins", 0);
            TestReflection.Invoke(_login, "TryDequeue");

            Assert.That(alice, Is.Empty, "nobody is there to answer; her connection id may be someone else's by now");
            Assert.That(Single(bob).Response.message, Is.EqualTo(UITextKeys.UI_ERROR_SERVICE_NOT_AVAILABLE), "bob's client is about to stop waiting: he is told, not checked");
            Assert.That(Single(carol).Response.message, Is.EqualTo(UITextKeys.UI_ERROR_INVALID_USERNAME_OR_PASSWORD));
            Assert.That(asked, Is.EqualTo(new[] { "carol" }), "the database only checks the login someone still waits for");
            Assert.That(TestReflection.GetField<int>(_login, "activeLogins"), Is.Zero);
            Assert.That(Single(Login(2, "bob")).Response.message, Is.EqualTo(UITextKeys.UI_ERROR_INVALID_USERNAME_OR_PASSWORD), "bob may try again");
        }

        [Test]
        public void TheServerEnforcesTheCharacterLimit()
        {
            _db.On<FindCharacterNameReq, FindCharacterNameResp>(nameof(IDatabaseClient.FindCharacterNameAsync), req => new FindCharacterNameResp() { FoundAmount = 0 });
            _db.On<CreateCharacterReq, CharacterResp>(nameof(IDatabaseClient.CreateCharacterAsync), req => new CharacterResp() { CharacterData = req.CharacterData });
            SignIn(1, "alice");
            _login.maxCharactersPerAccount = 3;

            AccountsHaveCharacters(3);
            Reply<ResponseCreateCharacterMessage> full = CreateCharacter(1, "Rowan");

            Assert.That(full.Code, Is.EqualTo(AckResponseCode.Error));
            Assert.That(full.Response.message, Is.EqualTo(UITextKeys.UI_ERROR_SERVICE_NOT_AVAILABLE));
            Assert.That(_db.LastRequest<GetCharactersReq>(nameof(IDatabaseClient.GetCharactersAsync)).UserId, Is.EqualTo("alice"));
            Assert.That(_db.WasCalled(nameof(IDatabaseClient.FindCharacterNameAsync)), Is.False, "an account at its limit cannot even probe for free names");
            Assert.That(_db.WasCalled(nameof(IDatabaseClient.CreateCharacterAsync)), Is.False);

            AccountsHaveCharacters(2);
            Assert.That(CreateCharacter(1, "Rowan").Code, Is.EqualTo(AckResponseCode.Success), "one below the limit");

            AccountsHaveCharacters(50);
            _login.maxCharactersPerAccount = 0;
            Assert.That(CreateCharacter(1, "Wren").Code, Is.EqualTo(AckResponseCode.Success), "0 is no limit");
        }

        [Test]
        public void ACharacterRequestFromAConnectionThatIsNotLoggedInNeverReachesTheDatabase()
        {
            Assert.That(CreateCharacter(1, "Rowan").Response.message, Is.EqualTo(UITextKeys.UI_ERROR_NOT_LOGGED_IN));
            Assert.That(CreateCharacter(1, "Ro wan").Response.message, Is.EqualTo(UITextKeys.UI_ERROR_NOT_LOGGED_IN), "not even its name is looked at");
            Assert.That(_db.Calls, Is.Empty);
        }

        [Test]
        public void AnAccountsSecondCreationWaitsForItsFirst()
        {
            SignIn(1, "alice");
            // The first creation is still with the database
            Assert.That(_login.FloodGuard.TryBeginCreateCharacter("alice"), Is.True);

            Assert.That(CreateCharacter(1, "Rowan").Response.message, Is.EqualTo(UITextKeys.UI_ERROR_TOO_MANY_REQUESTS));
            Assert.That(_db.WasCalled(nameof(IDatabaseClient.GetCharactersAsync)), Is.False);

            _login.FloodGuard.EndCreateCharacter("alice");
            AccountsHaveCharacters(0);
            Assert.That(CreateCharacter(1, "Rowan").Response.message, Is.Not.EqualTo(UITextKeys.UI_ERROR_TOO_MANY_REQUESTS));
            Assert.That(_login.FloodGuard.TryBeginCreateCharacter("alice"), Is.True, "a creation that ended, however it ended, leaves the account free to try again");
        }

        [Test]
        public void AConnectionOverItsRequestBudgetIsAnsweredWithoutReachingTheHandler()
        {
            _login.floodProtection.loginMaxRequestsPerConnection = 3;
            System.Action<double> wait = UseFloodClock();
            int handled = 0;
            RequestDelegate<EmptyMessage, ResponseChannelsMessage> handler = (requestHandler, request, result) =>
            {
                ++handled;
                result.InvokeSuccess(new ResponseChannelsMessage());
                return default;
            };
            System.Func<ResponseChannelsMessage> refusal = () => new ResponseChannelsMessage() { message = UITextKeys.UI_ERROR_TOO_MANY_REQUESTS };
            RequestDelegate<EmptyMessage, ResponseChannelsMessage> limited = (RequestDelegate<EmptyMessage, ResponseChannelsMessage>)typeof(LoginNetworkManager)
                .GetMethod("WithRequestLimit", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .MakeGenericMethod(typeof(EmptyMessage), typeof(ResponseChannelsMessage))
                .Invoke(_login, new object[] { handler, refusal });

            List<Reply<ResponseChannelsMessage>> replies = new List<Reply<ResponseChannelsMessage>>();
            RequestProceedResultDelegate<ResponseChannelsMessage> result = (code, response, extra) => replies.Add(new Reply<ResponseChannelsMessage>() { Code = code, Response = response });
            System.Action<long> send = connectionId => limited(new RequestHandlerData(0, 0, null, connectionId, new NetDataReader(new byte[0])), EmptyMessage.Value, result);

            for (int i = 0; i < 5; ++i)
                send(1);

            Assert.That(handled, Is.EqualTo(3), "the handler sees the budget and no more");
            Assert.That(replies, Has.Count.EqualTo(5), "every request is answered");
            Assert.That(replies.Take(3).Select(r => r.Code), Is.All.EqualTo(AckResponseCode.Success));
            Assert.That(replies.Skip(3).Select(r => r.Code), Is.All.EqualTo(AckResponseCode.Error));
            Assert.That(replies.Skip(3).Select(r => r.Response.message), Is.All.EqualTo(UITextKeys.UI_ERROR_TOO_MANY_REQUESTS));

            send(2);
            Assert.That(handled, Is.EqualTo(4), "another connection has its own budget");
            wait(10);
            send(1);
            Assert.That(handled, Is.EqualTo(5), "and the first has its back ten seconds later");
        }

        [Test]
        public void ClosingAConnectionForgetsItSoTheNextUserOfItsIdStartsClean()
        {
            PasswordsBelongTo(string.Empty);
            _login.MaxConcurrentRequest = 1;
            TestReflection.SetField(_login, "activeLogins", 1);
            Assert.That(Login(1, "alice"), Is.Empty, "alice waits in the queue");

            _login.OnPeerDisconnected(1, DisconnectReason.RemoteConnectionClose, SocketError.Success);

            // The transport gives id 1 to the next client, who logs in while alice's entry is still queued
            Assert.That(Login(1, "bob"), Is.Empty, "bob is not refused for alice's unfinished login; he queues");
        }

        private static int ReadLoginSetting(string[] args, int? configValue, int prefabValue, out int? written)
        {
            object[] parameters = { args, ProcessArguments.ARG_LOGIN_MAX_QUEUE, configValue, prefabValue };
            typeof(MMOServerInstance).GetMethod("ReadLoginSetting", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic).Invoke(null, parameters);
            written = (int?)parameters[2];
            return (int)parameters[3];
        }

        [Test]
        public void AFloodSettingComesFromTheArgumentThenTheConfigFileThenThePrefab()
        {
            string[] none = { "-batchmode" };

            Assert.That(ReadLoginSetting(new[] { "-batchmode", "-loginMaxQueue", "7" }, 500, 1000, out int? written), Is.EqualTo(7));
            Assert.That(written, Is.EqualTo(7), "the value in use goes back into the config");
            Assert.That(ReadLoginSetting(none, 500, 1000, out written), Is.EqualTo(500));
            Assert.That(written, Is.EqualTo(500));
            Assert.That(ReadLoginSetting(none, null, 1000, out written), Is.EqualTo(1000));
            Assert.That(written, Is.EqualTo(1000), "so a config file the server writes lists every setting");
            Assert.That(ReadLoginSetting(none, 0, 1000, out written), Is.Zero, "0, a limit switched off, is a value like any other");
            Assert.That(ReadLoginSetting(new[] { "-loginMaxQueue", "many" }, 500, 1000, out written), Is.EqualTo(500), "an argument that is no number is passed over");
        }

        [Test]
        public void StartingUpLeavesTheConfiguredConnectionLimitAlone()
        {
            // What -loginMaxConnections, the config file or the prefab set, before the server starts
            _login.maxConnections = 300;
            // Initialize also asks to hear when the game data has loaded; take that back afterwards
            System.Action onGameDataLoaded = (System.Action)System.Delegate.CreateDelegate(typeof(System.Action), _login, "GameInstance_OnGameDataLoadedEvent");
            try
            {
                TestReflection.Invoke(_login, "Initialize");

                Assert.That(_login.maxConnections, Is.EqualTo(300), "it used to be replaced with int.MaxValue here, so the setting never took effect");
            }
            finally
            {
                GameInstance.OnGameDataLoadedEvent -= onGameDataLoaded;
            }
        }
    }
}
#endif
