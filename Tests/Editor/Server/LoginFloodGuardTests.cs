using System.Net;
using MultiplayerARPG.MMO;
using NUnit.Framework;

namespace OpenMMORPG.Tests
{
    /// <summary>
    /// The login server's flood limits, on the class that counts them and with a clock the test
    /// moves by hand: no network, no database. A limit that is too loose lets one address spam
    /// accounts or guess passwords; one that is too tight, or that never forgets, locks real
    /// players out, so both edges of every limit are checked.
    /// </summary>
    [Category(TestCategories.Logic)]
    public class LoginFloodGuardTests : KitTestFixture
    {
        private static readonly IPAddress Home = IPAddress.Parse("203.0.113.5");
        private static readonly IPAddress Cafe = IPAddress.Parse("198.51.100.7");

        private LoginFloodProtectionSettings _settings;
        private LoginFloodGuard _guard;
        private long _now;
        private long _nextConnection;

        [SetUp]
        public void SetUp()
        {
            _settings = new LoginFloodProtectionSettings();
            // Starts well above zero, like a server that has been up for a while
            _now = 5000000;
            _nextConnection = 100;
            _guard = new LoginFloodGuard(_settings, () => _now);
        }

        private void Wait(double seconds)
        {
            _now += (long)System.Math.Round(seconds * 1000);
        }

        private long Connect(IPAddress address)
        {
            long connectionId = _nextConnection++;
            Assert.That(_guard.ConnectionOpened(connectionId, address), Is.True, "the connection is let in");
            return connectionId;
        }

        /// <summary>One login from start to answer. Returns what the guard said to it.</summary>
        private LoginFloodGuard.Verdict Login(long connectionId, string username, bool wrongPassword)
        {
            LoginFloodGuard.Verdict verdict = _guard.TryBeginLogin(connectionId, username, out LoginFloodGuard.LoginTicket ticket);
            if (verdict != LoginFloodGuard.Verdict.Allowed)
                return verdict;
            if (wrongPassword)
                _guard.LoginFailed(ticket);
            else
                _guard.LoginPasswordAccepted(ticket);
            _guard.EndLogin(ticket);
            return verdict;
        }

        /// <summary>One register request from start to answer.</summary>
        private LoginFloodGuard.Verdict Register(long connectionId, bool accountCreated)
        {
            LoginFloodGuard.Verdict verdict = _guard.TryBeginRegister(connectionId, out LoginFloodGuard.RegisterTicket ticket);
            if (verdict == LoginFloodGuard.Verdict.Allowed)
                _guard.EndRegister(ticket, accountCreated);
            return verdict;
        }

        // Defaults

        [Test]
        public void TheDefaultsAreTheDocumentedOnes()
        {
            LoginFloodProtectionSettings defaults = new LoginFloodProtectionSettings();

            Assert.That(defaults.loginMaxConnectionsPerAddress, Is.EqualTo(20));
            Assert.That(defaults.loginMaxRequestsPerConnection, Is.EqualTo(30));
            Assert.That(defaults.loginRequestWindowSeconds, Is.EqualTo(10));
            Assert.That(defaults.loginMaxAttemptsPerAddress, Is.EqualTo(30));
            Assert.That(defaults.loginAttemptWindowSeconds, Is.EqualTo(60));
            Assert.That(defaults.loginFailuresPerAddress, Is.EqualTo(10));
            Assert.That(defaults.loginFailuresPerUsername, Is.EqualTo(5));
            Assert.That(defaults.loginFailureWindowSeconds, Is.EqualTo(600));
            Assert.That(defaults.loginBackoffSeconds, Is.EqualTo(30));
            Assert.That(defaults.loginBackoffMaxSeconds, Is.EqualTo(300));
            Assert.That(defaults.loginMaxQueue, Is.EqualTo(1000));
            Assert.That(defaults.registerMaxAttemptsPerAddress, Is.EqualTo(20));
            Assert.That(defaults.registerAttemptWindowSeconds, Is.EqualTo(600));
            Assert.That(defaults.registerMaxAccountsPerAddress, Is.EqualTo(5));
            Assert.That(defaults.registerAccountWindowSeconds, Is.EqualTo(3600));
            Assert.That(defaults.loginExemptLoopback, Is.True);
            Assert.That(LoginNetworkManager.DefaultMaxConnections, Is.EqualTo(2000));
        }

        [Test]
        public void AnOrdinaryFirstSessionIsNeverRefused()
        {
            long connection = Connect(Home);

            // Channels, register, a mistyped password, login, character list, create, list, select
            for (int i = 0; i < 8; ++i)
                Assert.That(_guard.AllowRequest(connection), Is.EqualTo(LoginFloodGuard.Verdict.Allowed));
            Assert.That(Register(connection, true), Is.EqualTo(LoginFloodGuard.Verdict.Allowed));
            Assert.That(Login(connection, "alice", true), Is.EqualTo(LoginFloodGuard.Verdict.Allowed));
            Assert.That(Login(connection, "alice", false), Is.EqualTo(LoginFloodGuard.Verdict.Allowed));
            Assert.That(_guard.TryBeginCreateCharacter("alice"), Is.True);
            _guard.EndCreateCharacter("alice");

            int[] refusals = new int[LoginFloodGuard.VerdictCount];
            Assert.That(_guard.TakeRefusals(refusals), Is.False, "nothing was refused");
        }

        // Addresses and usernames

        [Test]
        public void AnIpv4AddressIsItsOwnKeyAndAnIpv6AddressSharesItsSlash64()
        {
            Assert.That(LoginFloodGuard.GetAddressKey(Home), Is.EqualTo("203.0.113.5"));
            Assert.That(LoginFloodGuard.GetAddressKey(IPAddress.Parse("::ffff:203.0.113.5")), Is.EqualTo("203.0.113.5"), "an IPv4 address written as IPv6 is the same address");

            string first = LoginFloodGuard.GetAddressKey(IPAddress.Parse("2001:db8:12:34:aaaa:bbbb:cccc:dddd"));
            string sameSubscriber = LoginFloodGuard.GetAddressKey(IPAddress.Parse("2001:db8:12:34::1"));
            string neighbour = LoginFloodGuard.GetAddressKey(IPAddress.Parse("2001:db8:12:35::1"));

            Assert.That(first, Is.EqualTo("2001:0db8:0012:0034::/64"));
            Assert.That(sameSubscriber, Is.EqualTo(first), "the addresses of one /64 cannot be told apart");
            Assert.That(neighbour, Is.Not.EqualTo(first));
            Assert.That(LoginFloodGuard.GetAddressKey(null), Is.Null);
        }

        [Test]
        public void AUsernameIsCountedWhateverItsCaseSpacesOrLength()
        {
            Assert.That(LoginFloodGuard.GetUsernameKey("  ALICE "), Is.EqualTo("alice"));
            Assert.That(LoginFloodGuard.GetUsernameKey("   "), Is.Null);
            Assert.That(LoginFloodGuard.GetUsernameKey(null), Is.Null);
            Assert.That(LoginFloodGuard.GetUsernameKey(new string('A', 5000)), Has.Length.EqualTo(LoginFloodGuard.MaxUsernameKeyLength), "a huge name cannot fill the server's memory");
        }

        // Connections

        [Test]
        public void AnAddressGetsOnlyItsShareOfConnections()
        {
            _settings.loginMaxConnectionsPerAddress = 3;
            long first = Connect(Home);
            Connect(Home);
            Connect(Home);

            Assert.That(_guard.ConnectionOpened(50, Home), Is.False, "the fourth is refused");
            Assert.That(_guard.CountConnections(Home), Is.EqualTo(3));
            Assert.That(_guard.AllowRequest(50), Is.EqualTo(LoginFloodGuard.Verdict.TooManyConnections), "and gets nothing done while it is being dropped");
            Assert.That(_guard.TryBeginLogin(50, "alice", out _), Is.EqualTo(LoginFloodGuard.Verdict.TooManyConnections));
            Assert.That(_guard.TryBeginRegister(50, out _), Is.EqualTo(LoginFloodGuard.Verdict.TooManyConnections));
            Assert.That(_guard.ConnectionOpened(51, Cafe), Is.True, "another address is not affected");

            _guard.ConnectionClosed(50);
            Assert.That(_guard.CountConnections(Home), Is.EqualTo(3), "dropping the refused one frees nothing");
            Assert.That(_guard.ConnectionOpened(52, Home), Is.False);
            _guard.ConnectionClosed(52);

            _guard.ConnectionClosed(first);
            Assert.That(_guard.ConnectionOpened(53, Home), Is.True, "a slot is free once a counted connection closes");
            Assert.That(_guard.CountConnections(Home), Is.EqualTo(3));
        }

        [Test]
        public void ClosingAConnectionTwiceOrOneNeverSeenChangesNothing()
        {
            long connection = Connect(Home);
            Connect(Home);

            _guard.ConnectionClosed(connection);
            _guard.ConnectionClosed(connection);
            _guard.ConnectionClosed(9999);

            Assert.That(_guard.CountConnections(Home), Is.EqualTo(1));
        }

        [Test]
        public void ThisMachineIsNotHeldToThePerAddressLimitsUnlessToldTo()
        {
            _settings.loginMaxConnectionsPerAddress = 1;
            _settings.registerMaxAccountsPerAddress = 1;
            long local = Connect(IPAddress.Loopback);
            Connect(IPAddress.IPv6Loopback);
            Connect(IPAddress.Parse("127.0.0.1"));

            Assert.That(Register(local, true), Is.EqualTo(LoginFloodGuard.Verdict.Allowed));
            Assert.That(Register(local, true), Is.EqualTo(LoginFloodGuard.Verdict.Allowed), "testing on one machine is not capped per address");

            _settings.loginExemptLoopback = false;
            Assert.That(_guard.ConnectionOpened(60, IPAddress.Loopback), Is.True);
            Assert.That(_guard.ConnectionOpened(61, IPAddress.Loopback), Is.False, "with the exemption off it is an address like any other");
        }

        [Test]
        public void AConnectionWithNoKnownAddressIsCountedOnItsOwn()
        {
            _settings.loginMaxConnectionsPerAddress = 1;
            _settings.registerMaxAccountsPerAddress = 1;

            Assert.That(_guard.ConnectionOpened(1, null), Is.True);
            Assert.That(_guard.ConnectionOpened(2, null), Is.True, "there is no address to cap the connections of");
            Assert.That(Register(1, true), Is.EqualTo(LoginFloodGuard.Verdict.Allowed));
            Assert.That(Register(1, true), Is.EqualTo(LoginFloodGuard.Verdict.TooManyAccounts), "but each connection is still limited");
            Assert.That(Register(2, true), Is.EqualTo(LoginFloodGuard.Verdict.Allowed));
            // A connection nobody announced (a test, or a transport without connect events) is treated the same
            Assert.That(Register(3, true), Is.EqualTo(LoginFloodGuard.Verdict.Allowed));
            Assert.That(Register(3, true), Is.EqualTo(LoginFloodGuard.Verdict.TooManyAccounts));
        }

        // Requests

        [Test]
        public void AConnectionGetsItsRequestBudgetPerWindow()
        {
            long connection = Connect(Home);
            long other = Connect(Home);

            for (int i = 0; i < 30; ++i)
            {
                Assert.That(_guard.AllowRequest(connection), Is.EqualTo(LoginFloodGuard.Verdict.Allowed), "request " + (i + 1));
                Wait(0.1);
            }
            Assert.That(_guard.AllowRequest(connection), Is.EqualTo(LoginFloodGuard.Verdict.TooManyRequests), "the 31st within ten seconds");
            Assert.That(_guard.AllowRequest(other), Is.EqualTo(LoginFloodGuard.Verdict.Allowed), "the budget is per connection");

            // The first request was 3 seconds ago, so it leaves the window 7 seconds from now
            Wait(6.9);
            Assert.That(_guard.AllowRequest(connection), Is.EqualTo(LoginFloodGuard.Verdict.TooManyRequests));
            Wait(0.1);
            Assert.That(_guard.AllowRequest(connection), Is.EqualTo(LoginFloodGuard.Verdict.Allowed), "one slot frees as the oldest request ages out");
            Assert.That(_guard.AllowRequest(connection), Is.EqualTo(LoginFloodGuard.Verdict.TooManyRequests), "only one");
        }

        [Test]
        public void RefusedRequestsDoNotUseUpTheBudget()
        {
            _settings.loginMaxRequestsPerConnection = 2;
            long connection = Connect(Home);
            _guard.AllowRequest(connection);
            _guard.AllowRequest(connection);

            for (int i = 0; i < 100; ++i)
                Assert.That(_guard.AllowRequest(connection), Is.EqualTo(LoginFloodGuard.Verdict.TooManyRequests));
            Wait(10);

            Assert.That(_guard.AllowRequest(connection), Is.EqualTo(LoginFloodGuard.Verdict.Allowed), "hammering while refused does not keep the connection shut out");
        }

        // Logins

        [Test]
        public void AConnectionHasOneLoginInProgressAtATime()
        {
            long connection = Connect(Home);

            Assert.That(_guard.TryBeginLogin(connection, "alice", out LoginFloodGuard.LoginTicket first), Is.EqualTo(LoginFloodGuard.Verdict.Allowed));
            Assert.That(_guard.TryBeginLogin(connection, "alice", out _), Is.EqualTo(LoginFloodGuard.Verdict.LoginInProgress));
            Assert.That(_guard.TryBeginLogin(Connect(Home), "alice", out _), Is.EqualTo(LoginFloodGuard.Verdict.Allowed), "another connection may");

            _guard.EndLogin(first);
            Assert.That(_guard.TryBeginLogin(connection, "alice", out _), Is.EqualTo(LoginFloodGuard.Verdict.Allowed), "once the first is answered");
        }

        [Test]
        public void LoginRequestsFromOneAddressAreCappedWhateverTheirOutcome()
        {
            long connection = Connect(Home);
            long sameAddress = Connect(Home);
            long elsewhere = Connect(Cafe);

            for (int i = 0; i < 30; ++i)
                Assert.That(Login(i % 2 == 0 ? connection : sameAddress, "user" + i, false), Is.EqualTo(LoginFloodGuard.Verdict.Allowed), "login " + (i + 1));

            Assert.That(Login(connection, "user30", false), Is.EqualTo(LoginFloodGuard.Verdict.TooManyLoginAttempts), "the 31st within a minute");
            Assert.That(Login(sameAddress, "user31", false), Is.EqualTo(LoginFloodGuard.Verdict.TooManyLoginAttempts), "a second connection does not help");
            Assert.That(Login(elsewhere, "user32", false), Is.EqualTo(LoginFloodGuard.Verdict.Allowed), "another address is not affected");

            Wait(60);
            Assert.That(Login(connection, "user33", false), Is.EqualTo(LoginFloodGuard.Verdict.Allowed), "a minute later");
        }

        [Test]
        public void WrongPasswordsPastTheFreeOnesMakeTheAddressWaitLongerEachTime()
        {
            long connection = Connect(Home);
            int attempt = 0;

            // Ten are free; a different username each time keeps the per-username limit out of it
            for (int i = 0; i < 10; ++i)
            {
                Assert.That(Login(connection, "user" + attempt++, true), Is.EqualTo(LoginFloodGuard.Verdict.Allowed), "wrong password " + (i + 1));
                Wait(2.1);
            }
            Assert.That(_guard.GetLoginWait(connection, "anyone"), Is.Zero, "no wait yet");

            // From the eleventh on: 30 s, 60 s, 120 s, 240 s, then the 300 s ceiling
            int[] waits = { 30, 60, 120, 240, 300 };
            foreach (int wait in waits)
            {
                Assert.That(Login(connection, "user" + attempt++, true), Is.EqualTo(LoginFloodGuard.Verdict.Allowed));
                Assert.That(_guard.GetLoginWait(connection, "anyone"), Is.EqualTo(wait * 1000L));
                Assert.That(Login(connection, "user" + attempt++, false), Is.EqualTo(LoginFloodGuard.Verdict.LoginBackoffAddress), "any login from the address waits, right password or not");
                Wait(wait - 0.001);
                Assert.That(Login(connection, "user" + attempt++, false), Is.EqualTo(LoginFloodGuard.Verdict.LoginBackoffAddress), "until the last millisecond");
                Wait(0.001);
                Assert.That(_guard.GetLoginWait(connection, "anyone"), Is.Zero);
            }
        }

        [Test]
        public void TheWaitNeverGrowsPastItsCeiling()
        {
            _settings.loginFailuresPerAddress = 1;
            _settings.loginFailureWindowSeconds = 100000;
            _settings.loginMaxAttemptsPerAddress = 0;
            long connection = Connect(Home);
            Login(connection, "first", true);

            long expected = 30;
            for (int i = 0; i < 40; ++i)
            {
                Assert.That(Login(connection, "user" + i, true), Is.EqualTo(LoginFloodGuard.Verdict.Allowed));
                Assert.That(_guard.GetLoginWait(connection, "anyone"), Is.EqualTo(expected * 1000), "wait " + (i + 1));
                Wait(expected);
                expected = System.Math.Min(expected * 2, 300);
            }
        }

        [Test]
        public void ARefusedLoginDoesNotExtendTheWaitOrCountAsAnAttempt()
        {
            _settings.loginFailuresPerAddress = 1;
            _settings.loginMaxAttemptsPerAddress = 4;
            long connection = Connect(Home);
            Login(connection, "a", true);
            Login(connection, "b", true);
            Assert.That(_guard.GetLoginWait(connection, "c"), Is.EqualTo(30000));

            for (int i = 0; i < 50; ++i)
                Assert.That(Login(connection, "c", true), Is.EqualTo(LoginFloodGuard.Verdict.LoginBackoffAddress));

            Assert.That(_guard.GetLoginWait(connection, "c"), Is.EqualTo(30000), "hammering during the wait does not lengthen it");
            Wait(30);
            Assert.That(Login(connection, "c", false), Is.EqualTo(LoginFloodGuard.Verdict.Allowed), "nor does it use up the attempts of the minute");
        }

        [Test]
        public void WrongPasswordsForOneUsernameMakeThatUsernameWaitFromAnyAddress()
        {
            // Five different addresses, one wrong password each: no address is over its own limit
            for (int i = 0; i < 5; ++i)
                Assert.That(Login(Connect(IPAddress.Parse("198.51.100." + (10 + i))), "Alice", true), Is.EqualTo(LoginFloodGuard.Verdict.Allowed));
            long sixth = Connect(IPAddress.Parse("198.51.100.99"));
            Assert.That(_guard.GetLoginWait(sixth, "alice"), Is.Zero, "five are free");

            Assert.That(Login(sixth, " ALICE ", true), Is.EqualTo(LoginFloodGuard.Verdict.Allowed));

            long owner = Connect(Home);
            Assert.That(Login(owner, "alice", false), Is.EqualTo(LoginFloodGuard.Verdict.LoginBackoffUsername), "the username waits, even for an address that never failed");
            Assert.That(_guard.GetLoginWait(owner, "alice"), Is.EqualTo(30000));
            Assert.That(Login(owner, "bob", false), Is.EqualTo(LoginFloodGuard.Verdict.Allowed), "other usernames do not");

            Wait(30);
            Assert.That(Login(owner, "alice", false), Is.EqualTo(LoginFloodGuard.Verdict.Allowed), "after the wait the owner gets in");
        }

        [Test]
        public void ARightPasswordClearsTheUsernamesRecordButNotTheAddresses()
        {
            _settings.loginFailuresPerAddress = 3;
            long connection = Connect(Home);
            for (int i = 0; i < 3; ++i)
                Login(connection, "alice", true);

            Assert.That(Login(connection, "alice", false), Is.EqualTo(LoginFloodGuard.Verdict.Allowed));

            // The username starts from nothing: five more are free for it, each from an address with a clean record
            for (int i = 0; i < 5; ++i)
                Assert.That(Login(Connect(IPAddress.Parse("198.51.100." + (20 + i))), "alice", true), Is.EqualTo(LoginFloodGuard.Verdict.Allowed));
            Assert.That(_guard.GetLoginWait(Connect(Cafe), "alice"), Is.Zero);

            // The address does not: its three are still counted, so the next wrong password makes it wait
            Assert.That(Login(connection, "bob", true), Is.EqualTo(LoginFloodGuard.Verdict.Allowed));
            Assert.That(_guard.GetLoginWait(connection, "carol"), Is.EqualTo(30000), "one good account does not wipe the record of guesses at others");
        }

        [Test]
        public void WrongPasswordsAreForgottenAfterTheWindowAndTheDoublingStartsOver()
        {
            _settings.loginFailuresPerAddress = 2;
            long connection = Connect(Home);
            Login(connection, "a", true);
            Login(connection, "b", true);
            Login(connection, "c", true);
            Wait(30);
            Login(connection, "d", true);
            Assert.That(_guard.GetLoginWait(connection, "e"), Is.EqualTo(60000), "the second wait is twice the first");

            Wait(600);

            Assert.That(Login(connection, "e", true), Is.EqualTo(LoginFloodGuard.Verdict.Allowed));
            Assert.That(Login(connection, "f", true), Is.EqualTo(LoginFloodGuard.Verdict.Allowed));
            Assert.That(_guard.GetLoginWait(connection, "g"), Is.Zero, "two are free again");
            Login(connection, "g", true);
            Assert.That(_guard.GetLoginWait(connection, "h"), Is.EqualTo(30000), "and the wait starts from the beginning");
        }

        [Test]
        public void ASteadyTrickleOfMistakesFromASharedAddressNeverMakesItWait()
        {
            // An internet cafe: someone mistypes a password every 90 seconds, all day
            long connection = Connect(Cafe);
            for (int i = 0; i < 200; ++i)
            {
                Assert.That(Login(connection, "player" + i, true), Is.EqualTo(LoginFloodGuard.Verdict.Allowed), "mistake " + (i + 1));
                Wait(90);
            }
        }

        [Test]
        public void ALoginOutlivedByItsConnectionIsToldApartFromTheNextUserOfThatId()
        {
            Assert.That(_guard.ConnectionOpened(7, Home), Is.True);
            Assert.That(_guard.TryBeginLogin(7, "alice", out LoginFloodGuard.LoginTicket ticket), Is.EqualTo(LoginFloodGuard.Verdict.Allowed));
            Assert.That(_guard.IsStillConnected(ticket), Is.True);

            _guard.ConnectionClosed(7);
            Assert.That(_guard.IsStillConnected(ticket), Is.False);

            // The transport hands the id to the next client
            Assert.That(_guard.ConnectionOpened(7, Cafe), Is.True);
            Assert.That(_guard.IsStillConnected(ticket), Is.False, "the same id is not the same connection");
            Assert.That(_guard.TryBeginLogin(7, "bob", out LoginFloodGuard.LoginTicket bob), Is.EqualTo(LoginFloodGuard.Verdict.Allowed), "the newcomer is not blocked by the old login");

            _guard.EndLogin(ticket);
            Assert.That(_guard.TryBeginLogin(7, "bob", out _), Is.EqualTo(LoginFloodGuard.Verdict.LoginInProgress), "and the old login ending does not end the newcomer's");
            _guard.EndLogin(bob);
        }

        // Registration

        [Test]
        public void RegisterRequestsFromOneAddressAreCappedWhateverTheirOutcome()
        {
            long connection = Connect(Home);

            for (int i = 0; i < 20; ++i)
                Assert.That(Register(connection, false), Is.EqualTo(LoginFloodGuard.Verdict.Allowed), "request " + (i + 1));

            Assert.That(Register(connection, false), Is.EqualTo(LoginFloodGuard.Verdict.TooManyRegisterAttempts), "the 21st within ten minutes");
            Assert.That(Register(Connect(Home), false), Is.EqualTo(LoginFloodGuard.Verdict.TooManyRegisterAttempts), "a second connection does not help");
            Assert.That(Register(Connect(Cafe), false), Is.EqualTo(LoginFloodGuard.Verdict.Allowed));

            Wait(600);
            Assert.That(Register(connection, false), Is.EqualTo(LoginFloodGuard.Verdict.Allowed));
        }

        [Test]
        public void AccountsCreatedFromOneAddressAreCapped()
        {
            long connection = Connect(Home);

            for (int i = 0; i < 5; ++i)
            {
                Assert.That(Register(connection, true), Is.EqualTo(LoginFloodGuard.Verdict.Allowed), "account " + (i + 1));
                Wait(60);
            }
            Assert.That(Register(connection, true), Is.EqualTo(LoginFloodGuard.Verdict.TooManyAccounts), "the sixth within the hour");
            Assert.That(Register(Connect(Cafe), true), Is.EqualTo(LoginFloodGuard.Verdict.Allowed));

            // The first account was made 5 minutes ago
            Wait(54 * 60);
            Assert.That(Register(connection, true), Is.EqualTo(LoginFloodGuard.Verdict.TooManyAccounts), "59 minutes after the first");
            Wait(60);
            Assert.That(Register(connection, true), Is.EqualTo(LoginFloodGuard.Verdict.Allowed), "an hour after the first, one more");
        }

        [Test]
        public void ARegistrationThatCreatesNothingGivesItsAccountSlotBack()
        {
            _settings.registerMaxAccountsPerAddress = 2;
            long connection = Connect(Home);

            // Taken names, short passwords: plenty of tries that make no account
            for (int i = 0; i < 10; ++i)
                Assert.That(Register(connection, false), Is.EqualTo(LoginFloodGuard.Verdict.Allowed));

            Assert.That(Register(connection, true), Is.EqualTo(LoginFloodGuard.Verdict.Allowed));
            Assert.That(Register(connection, true), Is.EqualTo(LoginFloodGuard.Verdict.Allowed));
            Assert.That(Register(connection, true), Is.EqualTo(LoginFloodGuard.Verdict.TooManyAccounts));
        }

        [Test]
        public void RegistrationsSentTogetherCannotOvershootTheAccountLimit()
        {
            _settings.registerMaxAccountsPerAddress = 2;
            long connection = Connect(Home);

            // Three arrive before the database has answered any
            Assert.That(_guard.TryBeginRegister(connection, out LoginFloodGuard.RegisterTicket first), Is.EqualTo(LoginFloodGuard.Verdict.Allowed));
            Assert.That(_guard.TryBeginRegister(connection, out LoginFloodGuard.RegisterTicket second), Is.EqualTo(LoginFloodGuard.Verdict.Allowed));
            Assert.That(_guard.TryBeginRegister(connection, out _), Is.EqualTo(LoginFloodGuard.Verdict.TooManyAccounts));

            _guard.EndRegister(first, false);
            Assert.That(_guard.TryBeginRegister(connection, out LoginFloodGuard.RegisterTicket third), Is.EqualTo(LoginFloodGuard.Verdict.Allowed), "the failed one made room");
            _guard.EndRegister(second, true);
            _guard.EndRegister(third, true);
            Assert.That(Register(connection, true), Is.EqualTo(LoginFloodGuard.Verdict.TooManyAccounts));
        }

        // Characters

        [Test]
        public void AnAccountCreatesOneCharacterAtATime()
        {
            Assert.That(_guard.TryBeginCreateCharacter("alice"), Is.True);
            Assert.That(_guard.TryBeginCreateCharacter("alice"), Is.False);
            Assert.That(_guard.TryBeginCreateCharacter("bob"), Is.True);

            _guard.EndCreateCharacter("alice");
            Assert.That(_guard.TryBeginCreateCharacter("alice"), Is.True);
        }

        // Switching off, reporting, memory

        [Test]
        public void ZeroTurnsALimitOff()
        {
            _settings.loginMaxConnectionsPerAddress = 0;
            _settings.loginMaxRequestsPerConnection = 0;
            _settings.loginMaxAttemptsPerAddress = 0;
            _settings.loginFailuresPerAddress = 0;
            _settings.loginFailuresPerUsername = 0;
            _settings.registerMaxAttemptsPerAddress = 0;
            _settings.registerMaxAccountsPerAddress = 0;
            long connection = Connect(Home);

            for (int i = 0; i < 200; ++i)
            {
                Assert.That(_guard.ConnectionOpened(1000 + i, Home), Is.True);
                Assert.That(_guard.AllowRequest(connection), Is.EqualTo(LoginFloodGuard.Verdict.Allowed));
                Assert.That(Login(connection, "alice", true), Is.EqualTo(LoginFloodGuard.Verdict.Allowed));
                Assert.That(Register(connection, true), Is.EqualTo(LoginFloodGuard.Verdict.Allowed));
            }
            Assert.That(_guard.TrackedKeyCount, Is.Zero, "and nothing is kept for a limit that is off");
        }

        [Test]
        public void TurningTheBackoffOffLiftsAWaitAlreadyRunning()
        {
            _settings.loginFailuresPerUsername = 1;
            long connection = Connect(Home);
            Login(connection, "alice", true);
            Login(connection, "alice", true);
            Assert.That(Login(connection, "alice", false), Is.EqualTo(LoginFloodGuard.Verdict.LoginBackoffUsername));

            _settings.loginFailuresPerUsername = 0;

            Assert.That(Login(connection, "alice", false), Is.EqualTo(LoginFloodGuard.Verdict.Allowed));
        }

        [Test]
        public void RefusalsAreCountedByReasonAndHandedOutOnce()
        {
            _settings.loginMaxRequestsPerConnection = 1;
            _settings.registerMaxAccountsPerAddress = 1;
            long connection = Connect(Home);
            _guard.AllowRequest(connection);
            _guard.AllowRequest(connection);
            _guard.AllowRequest(connection);
            Register(connection, true);
            Register(connection, true);

            int[] refusals = new int[LoginFloodGuard.VerdictCount];
            Assert.That(_guard.TakeRefusals(refusals), Is.True);
            Assert.That(refusals[(int)LoginFloodGuard.Verdict.TooManyRequests], Is.EqualTo(2));
            Assert.That(refusals[(int)LoginFloodGuard.Verdict.TooManyAccounts], Is.EqualTo(1));
            Assert.That(refusals[(int)LoginFloodGuard.Verdict.Allowed], Is.Zero);

            Assert.That(_guard.TakeRefusals(refusals), Is.False, "the counts start over");
            Assert.That(refusals[(int)LoginFloodGuard.Verdict.TooManyRequests], Is.Zero);
        }

        [Test]
        public void WhatIsCountedIsForgottenOnceItsWindowHasPassed()
        {
            for (int i = 0; i < 50; ++i)
            {
                long connection = Connect(IPAddress.Parse("198.51.100." + i));
                Login(connection, "user" + i, true);
                Register(connection, true);
                _guard.ConnectionClosed(connection);
            }
            Assert.That(_guard.TrackedKeyCount, Is.GreaterThanOrEqualTo(200), "attempts, accounts and failures are tracked per address and username");

            // Longer than the longest window (accounts: an hour); any later call does the clean-up
            Wait(3601);
            _guard.AllowRequest(Connect(Home));

            Assert.That(_guard.TrackedKeyCount, Is.Zero, "a server that has been attacked does not carry the addresses around for ever");
        }

        [Test]
        public void ARunningWaitIsNotForgottenByTheCleanUp()
        {
            _settings.loginFailuresPerAddress = 1;
            _settings.loginFailureWindowSeconds = 10;
            _settings.loginBackoffSeconds = 120;
            long connection = Connect(Home);
            Login(connection, "a", true);
            Login(connection, "b", true);

            // The failures have left their ten second window, the two minute wait has not ended
            Wait(60);
            _guard.AllowRequest(connection);

            Assert.That(Login(connection, "c", false), Is.EqualTo(LoginFloodGuard.Verdict.LoginBackoffAddress));
        }
    }
}
