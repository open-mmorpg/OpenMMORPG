using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace MultiplayerARPG.MMO
{
    /// <summary>
    /// The login server's flood limits. Every count is "this many per window"; 0 turns that limit off.
    /// They blunt abuse from a few addresses (account spam, password guessing, queue stuffing, a client
    /// hammering requests). They are no defence against a volumetric network flood.
    /// The field names are also the serverConfig.json keys and, with a leading dash, the arguments.
    /// </summary>
    [Serializable]
    public class LoginFloodProtectionSettings
    {
        // Connections open at once from one address
        public int loginMaxConnectionsPerAddress = 20;
        // Requests of any kind from one connection
        public int loginMaxRequestsPerConnection = 30;
        public int loginRequestWindowSeconds = 10;
        // Login requests from one address, whatever their outcome
        public int loginMaxAttemptsPerAddress = 30;
        public int loginAttemptWindowSeconds = 60;
        // Wrong passwords allowed in a window before each further one adds a wait
        public int loginFailuresPerAddress = 10;
        public int loginFailuresPerUsername = 5;
        public int loginFailureWindowSeconds = 600;
        // The first wait; it doubles with every further wrong password, up to the maximum
        public int loginBackoffSeconds = 30;
        public int loginBackoffMaxSeconds = 300;
        // Logins waiting for a free slot (see MaxConcurrentRequest)
        public int loginMaxQueue = 1000;
        // Register requests from one address, whatever their outcome
        public int registerMaxAttemptsPerAddress = 20;
        public int registerAttemptWindowSeconds = 600;
        // Accounts created from one address
        public int registerMaxAccountsPerAddress = 5;
        public int registerAccountWindowSeconds = 3600;
        // Connections from this machine (127.0.0.1, ::1) are not held to the per-address limits,
        // so testing on one machine and a proxy on the same host are not throttled as one player
        public bool loginExemptLoopback = true;
    }

    /// <summary>
    /// Counts what each connection, address and username has done lately and says whether the next
    /// request may go ahead. It holds no network or Unity types beyond <see cref="IPAddress"/>, so it
    /// runs the same in Unity and in the .NET host and can be tested with a fake clock.
    /// All state is in memory: it is lost on restart and is not shared between login servers.
    /// </summary>
    public class LoginFloodGuard
    {
        public enum Verdict : byte
        {
            Allowed,
            /// <summary>The address already has its share of connections.</summary>
            TooManyConnections,
            /// <summary>The connection used up its request budget.</summary>
            TooManyRequests,
            /// <summary>The connection already has a login waiting or running.</summary>
            LoginInProgress,
            TooManyLoginAttempts,
            /// <summary>Too many wrong passwords from the address; it has to wait.</summary>
            LoginBackoffAddress,
            /// <summary>Too many wrong passwords for the username; it has to wait.</summary>
            LoginBackoffUsername,
            TooManyRegisterAttempts,
            TooManyAccounts,
        }

        /// <summary>What a login was let in as, to report its outcome against the same keys later.</summary>
        public struct LoginTicket
        {
            public long connectionId;
            public long serial;
            public string addressKey;
            public string usernameKey;
            public long startedAt;
        }

        /// <summary>A register request that was let in, holding one of its address's account slots.</summary>
        public struct RegisterTicket
        {
            public string addressKey;
            public bool holdsAccountSlot;
        }

        /// <summary>Above this many tracked keys a table stops taking new ones until old ones expire.</summary>
        public const int MaxTrackedKeys = 100000;
        public const int MaxUsernameKeyLength = 64;
        private const long SweepIntervalMilliseconds = 30000;
        private static readonly int s_verdictCount = Enum.GetValues(typeof(Verdict)).Length;

        private sealed class Connection
        {
            public long serial;
            // null when the per-address limits do not apply to it
            public string addressKey;
            public bool countedForAddress;
            public bool refused;
            public bool loginInProgress;
            public readonly List<long> requests = new List<long>();
        }

        private sealed class Backoff
        {
            public readonly List<long> failures = new List<long>();
            public int strikes;
            public long blockedUntil;
        }

        private readonly object _gate = new object();
        private readonly LoginFloodProtectionSettings _settings;
        private readonly Func<long> _clock;
        private readonly Dictionary<long, Connection> _connections = new Dictionary<long, Connection>();
        private readonly Dictionary<string, int> _connectionsByAddress = new Dictionary<string, int>();
        private readonly Dictionary<string, List<long>> _loginAttempts = new Dictionary<string, List<long>>();
        private readonly Dictionary<string, List<long>> _registerAttempts = new Dictionary<string, List<long>>();
        private readonly Dictionary<string, List<long>> _accounts = new Dictionary<string, List<long>>();
        private readonly Dictionary<string, Backoff> _addressFailures = new Dictionary<string, Backoff>();
        private readonly Dictionary<string, Backoff> _usernameFailures = new Dictionary<string, Backoff>();
        private readonly HashSet<string> _creatingCharacters = new HashSet<string>();
        private readonly int[] _refusals = new int[s_verdictCount];
        private long _serialCounter;
        private long _lastSweep;

        public LoginFloodProtectionSettings Settings { get { return _settings; } }

        /// <param name="clockMilliseconds">A clock that never goes back, in milliseconds. Tests pass their own.</param>
        public LoginFloodGuard(LoginFloodProtectionSettings settings, Func<long> clockMilliseconds = null)
        {
            _settings = settings ?? new LoginFloodProtectionSettings();
            _clock = clockMilliseconds ?? MonotonicMilliseconds;
            _lastSweep = _clock();
        }

        private static long MonotonicMilliseconds()
        {
            return (long)(Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency);
        }

        public long Now { get { return _clock(); } }

        #region Keys
        /// <summary>
        /// The key an address is counted under: an IPv4 address as it is, an IPv6 address by its /64
        /// (one subscriber usually owns a whole /64). Null for no address.
        /// </summary>
        public static string GetAddressKey(IPAddress address)
        {
            if (address == null)
                return null;
            if (address.IsIPv4MappedToIPv6)
                address = address.MapToIPv4();
            if (address.AddressFamily == AddressFamily.InterNetworkV6)
            {
                byte[] bytes = address.GetAddressBytes();
                return string.Format("{0:x2}{1:x2}:{2:x2}{3:x2}:{4:x2}{5:x2}:{6:x2}{7:x2}::/64",
                    bytes[0], bytes[1], bytes[2], bytes[3], bytes[4], bytes[5], bytes[6], bytes[7]);
            }
            return address.ToString();
        }

        public static bool IsLoopback(IPAddress address)
        {
            if (address == null)
                return false;
            if (address.IsIPv4MappedToIPv6)
                address = address.MapToIPv4();
            return IPAddress.IsLoopback(address);
        }

        /// <summary>The key a username is counted under: trimmed, lower case and cut to a fixed length. Null when empty.</summary>
        public static string GetUsernameKey(string username)
        {
            if (string.IsNullOrEmpty(username))
                return null;
            username = username.Trim();
            if (username.Length == 0)
                return null;
            if (username.Length > MaxUsernameKeyLength)
                username = username.Substring(0, MaxUsernameKeyLength);
            return username.ToLowerInvariant();
        }
        #endregion

        #region Connections
        /// <summary>
        /// Call when a connection opens. Pass a null address when the transport cannot tell it: the
        /// per-address limits then count that connection on its own. Returns false when the address
        /// already has its share of connections; the caller should then disconnect it. Until
        /// <see cref="ConnectionClosed"/> every request from a refused connection is refused too.
        /// </summary>
        public bool ConnectionOpened(long connectionId, IPAddress address)
        {
            lock (_gate)
            {
                long now = _clock();
                Sweep(now);
                ForgetConnection(connectionId);
                Connection connection = new Connection();
                connection.serial = ++_serialCounter;
                _connections[connectionId] = connection;
                if (address == null)
                {
                    connection.addressKey = ConnectionKey(connectionId);
                    return true;
                }
                if (_settings.loginExemptLoopback && IsLoopback(address))
                    return true;
                connection.addressKey = GetAddressKey(address);
                int open;
                _connectionsByAddress.TryGetValue(connection.addressKey, out open);
                int max = _settings.loginMaxConnectionsPerAddress;
                if (max > 0 && open >= max)
                {
                    connection.refused = true;
                    Refuse(Verdict.TooManyConnections);
                    return false;
                }
                _connectionsByAddress[connection.addressKey] = open + 1;
                connection.countedForAddress = true;
                return true;
            }
        }

        public void ConnectionClosed(long connectionId)
        {
            lock (_gate)
            {
                ForgetConnection(connectionId);
            }
        }

        private void ForgetConnection(long connectionId)
        {
            Connection connection;
            if (!_connections.TryGetValue(connectionId, out connection))
                return;
            _connections.Remove(connectionId);
            if (!connection.countedForAddress)
                return;
            int open;
            if (_connectionsByAddress.TryGetValue(connection.addressKey, out open) && open > 1)
                _connectionsByAddress[connection.addressKey] = open - 1;
            else
                _connectionsByAddress.Remove(connection.addressKey);
        }

        public int CountConnections(IPAddress address)
        {
            lock (_gate)
            {
                int open;
                string key = GetAddressKey(address);
                return key != null && _connectionsByAddress.TryGetValue(key, out open) ? open : 0;
            }
        }

        private static string ConnectionKey(long connectionId)
        {
            return "#" + connectionId;
        }

        /// <summary>
        /// A connection nobody announced with <see cref="ConnectionOpened"/> is counted on its own,
        /// as one whose address is unknown.
        /// </summary>
        private Connection GetConnection(long connectionId)
        {
            Connection connection;
            if (!_connections.TryGetValue(connectionId, out connection))
            {
                connection = new Connection();
                connection.serial = ++_serialCounter;
                connection.addressKey = ConnectionKey(connectionId);
                _connections[connectionId] = connection;
            }
            return connection;
        }
        #endregion

        #region Requests
        /// <summary>Call for every request a connection sends, of any kind.</summary>
        public Verdict AllowRequest(long connectionId)
        {
            lock (_gate)
            {
                long now = _clock();
                Sweep(now);
                Connection connection = GetConnection(connectionId);
                if (connection.refused)
                    return Refuse(Verdict.TooManyConnections);
                if (!TryCount(connection.requests, now, _settings.loginMaxRequestsPerConnection, _settings.loginRequestWindowSeconds))
                    return Refuse(Verdict.TooManyRequests);
                return Verdict.Allowed;
            }
        }
        #endregion

        #region Login
        /// <summary>
        /// Call when a login request arrives, before it touches the database. When it is allowed the
        /// connection is marked as having a login in progress until <see cref="EndLogin"/>.
        /// </summary>
        public Verdict TryBeginLogin(long connectionId, string username, out LoginTicket ticket)
        {
            lock (_gate)
            {
                long now = _clock();
                Sweep(now);
                Connection connection = GetConnection(connectionId);
                ticket = new LoginTicket()
                {
                    connectionId = connectionId,
                    serial = connection.serial,
                    addressKey = connection.addressKey,
                    usernameKey = GetUsernameKey(username),
                    startedAt = now,
                };
                if (connection.refused)
                    return Refuse(Verdict.TooManyConnections);
                if (connection.loginInProgress)
                    return Refuse(Verdict.LoginInProgress);
                // The waits first: a login refused because of one is not held against the attempt limit
                if (_settings.loginFailuresPerAddress > 0 && IsBlocked(_addressFailures, ticket.addressKey, now))
                    return Refuse(Verdict.LoginBackoffAddress);
                if (_settings.loginFailuresPerUsername > 0 && IsBlocked(_usernameFailures, ticket.usernameKey, now))
                    return Refuse(Verdict.LoginBackoffUsername);
                if (!TryCount(_loginAttempts, ticket.addressKey, now, _settings.loginMaxAttemptsPerAddress, _settings.loginAttemptWindowSeconds))
                    return Refuse(Verdict.TooManyLoginAttempts);
                connection.loginInProgress = true;
                return Verdict.Allowed;
            }
        }

        /// <summary>The username or password was wrong.</summary>
        public void LoginFailed(LoginTicket ticket)
        {
            lock (_gate)
            {
                long now = _clock();
                AddFailure(_addressFailures, ticket.addressKey, now, _settings.loginFailuresPerAddress);
                AddFailure(_usernameFailures, ticket.usernameKey, now, _settings.loginFailuresPerUsername);
            }
        }

        /// <summary>
        /// The password was right, so whoever sent it owns the account: forget the wrong passwords
        /// held against the username. Those held against the address stay, or one good account
        /// would wipe the record of guesses at others.
        /// </summary>
        public void LoginPasswordAccepted(LoginTicket ticket)
        {
            lock (_gate)
            {
                if (ticket.usernameKey != null)
                    _usernameFailures.Remove(ticket.usernameKey);
            }
        }

        /// <summary>Call once for every login <see cref="TryBeginLogin"/> allowed, when it has been answered.</summary>
        public void EndLogin(LoginTicket ticket)
        {
            lock (_gate)
            {
                Connection connection;
                if (_connections.TryGetValue(ticket.connectionId, out connection) && connection.serial == ticket.serial)
                    connection.loginInProgress = false;
            }
        }

        /// <summary>False when the connection the login came from has closed (its id may belong to someone else by now).</summary>
        public bool IsStillConnected(LoginTicket ticket)
        {
            lock (_gate)
            {
                Connection connection;
                return _connections.TryGetValue(ticket.connectionId, out connection) && connection.serial == ticket.serial;
            }
        }

        /// <summary>How long the next login from this connection for this username has to wait, in milliseconds. 0 when it need not.</summary>
        public long GetLoginWait(long connectionId, string username)
        {
            lock (_gate)
            {
                long now = _clock();
                Connection connection;
                string addressKey = _connections.TryGetValue(connectionId, out connection) ? connection.addressKey : ConnectionKey(connectionId);
                long wait = _settings.loginFailuresPerAddress > 0 ? RemainingBlock(_addressFailures, addressKey, now) : 0;
                if (_settings.loginFailuresPerUsername > 0)
                    wait = Math.Max(wait, RemainingBlock(_usernameFailures, GetUsernameKey(username), now));
                return wait;
            }
        }
        #endregion

        #region Register
        /// <summary>
        /// Call when a register request arrives, before it touches the database. When it is allowed
        /// it holds one of the address's account slots; give it back with <see cref="EndRegister"/>
        /// if no account was created.
        /// </summary>
        public Verdict TryBeginRegister(long connectionId, out RegisterTicket ticket)
        {
            lock (_gate)
            {
                long now = _clock();
                Sweep(now);
                Connection connection = GetConnection(connectionId);
                ticket = new RegisterTicket() { addressKey = connection.addressKey };
                if (connection.refused)
                    return Refuse(Verdict.TooManyConnections);
                // Check the account slots without taking one, so a refused request costs nothing
                if (_settings.registerMaxAccountsPerAddress > 0 && ticket.addressKey != null &&
                    Count(_accounts, ticket.addressKey, now, _settings.registerAccountWindowSeconds) >= _settings.registerMaxAccountsPerAddress)
                    return Refuse(Verdict.TooManyAccounts);
                if (!TryCount(_registerAttempts, ticket.addressKey, now, _settings.registerMaxAttemptsPerAddress, _settings.registerAttemptWindowSeconds))
                    return Refuse(Verdict.TooManyRegisterAttempts);
                if (_settings.registerMaxAccountsPerAddress > 0 && ticket.addressKey != null)
                    ticket.holdsAccountSlot = TryCount(_accounts, ticket.addressKey, now, _settings.registerMaxAccountsPerAddress, _settings.registerAccountWindowSeconds);
                return Verdict.Allowed;
            }
        }

        /// <summary>Call once for every register request <see cref="TryBeginRegister"/> allowed.</summary>
        public void EndRegister(RegisterTicket ticket, bool accountCreated)
        {
            if (accountCreated || !ticket.holdsAccountSlot)
                return;
            lock (_gate)
            {
                List<long> times;
                if (_accounts.TryGetValue(ticket.addressKey, out times) && times.Count > 0)
                    times.RemoveAt(times.Count - 1);
            }
        }
        #endregion

        #region Characters
        /// <summary>
        /// Lets one character creation run per account at a time, so two requests cannot both pass
        /// the characters-per-account check. Pair with <see cref="EndCreateCharacter"/>.
        /// </summary>
        public bool TryBeginCreateCharacter(string userId)
        {
            lock (_gate)
            {
                return _creatingCharacters.Add(userId);
            }
        }

        public void EndCreateCharacter(string userId)
        {
            lock (_gate)
            {
                _creatingCharacters.Remove(userId);
            }
        }
        #endregion

        #region Reporting
        /// <summary>How many requests each verdict has refused since the last call. Indexed by <see cref="Verdict"/>.</summary>
        public bool TakeRefusals(int[] counts)
        {
            lock (_gate)
            {
                bool any = false;
                for (int i = 0; i < _refusals.Length && i < counts.Length; ++i)
                {
                    counts[i] = _refusals[i];
                    any |= _refusals[i] > 0;
                    _refusals[i] = 0;
                }
                return any;
            }
        }

        public static int VerdictCount { get { return s_verdictCount; } }

        private Verdict Refuse(Verdict verdict)
        {
            _refusals[(int)verdict]++;
            return verdict;
        }
        #endregion

        #region Counting
        private static long WindowMilliseconds(int seconds)
        {
            return seconds * 1000L;
        }

        private static void DropOlderThan(List<long> times, long now, long windowMilliseconds)
        {
            int expired = 0;
            while (expired < times.Count && now - times[expired] >= windowMilliseconds)
                ++expired;
            if (expired > 0)
                times.RemoveRange(0, expired);
        }

        /// <summary>Counts one more against a limit. False when the limit is already used up.</summary>
        private static bool TryCount(List<long> times, long now, int limit, int windowSeconds)
        {
            if (limit <= 0 || windowSeconds <= 0)
                return true;
            DropOlderThan(times, now, WindowMilliseconds(windowSeconds));
            if (times.Count >= limit)
                return false;
            times.Add(now);
            return true;
        }

        private bool TryCount(Dictionary<string, List<long>> table, string key, long now, int limit, int windowSeconds)
        {
            if (key == null || limit <= 0 || windowSeconds <= 0)
                return true;
            List<long> times;
            if (!table.TryGetValue(key, out times))
            {
                if (table.Count >= MaxTrackedKeys)
                    return true;
                times = new List<long>();
                table[key] = times;
            }
            return TryCount(times, now, limit, windowSeconds);
        }

        private static int Count(Dictionary<string, List<long>> table, string key, long now, int windowSeconds)
        {
            List<long> times;
            if (key == null || !table.TryGetValue(key, out times))
                return 0;
            DropOlderThan(times, now, WindowMilliseconds(windowSeconds));
            return times.Count;
        }

        private static long RemainingBlock(Dictionary<string, Backoff> table, string key, long now)
        {
            Backoff backoff;
            if (key == null || !table.TryGetValue(key, out backoff))
                return 0;
            return Math.Max(0, backoff.blockedUntil - now);
        }

        private static bool IsBlocked(Dictionary<string, Backoff> table, string key, long now)
        {
            return RemainingBlock(table, key, now) > 0;
        }

        /// <summary>
        /// Notes a wrong password. The first <paramref name="free"/> in a window cost nothing; each
        /// one after that makes the key wait, twice as long as the time before, up to the maximum.
        /// The doubling starts over once a whole window has passed without a wrong password.
        /// </summary>
        private void AddFailure(Dictionary<string, Backoff> table, string key, long now, int free)
        {
            if (key == null || free <= 0 || _settings.loginFailureWindowSeconds <= 0)
                return;
            Backoff backoff;
            if (!table.TryGetValue(key, out backoff))
            {
                if (table.Count >= MaxTrackedKeys)
                    return;
                backoff = new Backoff();
                table[key] = backoff;
            }
            DropOlderThan(backoff.failures, now, WindowMilliseconds(_settings.loginFailureWindowSeconds));
            if (backoff.failures.Count == 0)
                backoff.strikes = 0;
            backoff.failures.Add(now);
            // Only "more than the free ones" matters, so the list never needs to grow past that
            if (backoff.failures.Count > free + 1)
                backoff.failures.RemoveAt(0);
            if (backoff.failures.Count <= free)
                return;
            backoff.strikes++;
            long wait = WindowMilliseconds(Math.Max(1, _settings.loginBackoffSeconds));
            long max = Math.Max(wait, WindowMilliseconds(_settings.loginBackoffMaxSeconds));
            for (int i = 1; i < backoff.strikes && wait < max; ++i)
                wait *= 2;
            backoff.blockedUntil = now + Math.Min(wait, max);
        }

        /// <summary>Forgets keys that have nothing left inside their window. Runs at most every 30 seconds.</summary>
        private void Sweep(long now)
        {
            if (now - _lastSweep < SweepIntervalMilliseconds)
                return;
            _lastSweep = now;
            SweepTable(_loginAttempts, now, _settings.loginAttemptWindowSeconds);
            SweepTable(_registerAttempts, now, _settings.registerAttemptWindowSeconds);
            SweepTable(_accounts, now, _settings.registerAccountWindowSeconds);
            SweepTable(_addressFailures, now);
            SweepTable(_usernameFailures, now);
        }

        private readonly List<string> _expiredKeys = new List<string>();

        private void SweepTable(Dictionary<string, List<long>> table, long now, int windowSeconds)
        {
            _expiredKeys.Clear();
            foreach (KeyValuePair<string, List<long>> entry in table)
            {
                DropOlderThan(entry.Value, now, WindowMilliseconds(windowSeconds));
                if (entry.Value.Count == 0)
                    _expiredKeys.Add(entry.Key);
            }
            for (int i = 0; i < _expiredKeys.Count; ++i)
                table.Remove(_expiredKeys[i]);
            _expiredKeys.Clear();
        }

        private void SweepTable(Dictionary<string, Backoff> table, long now)
        {
            _expiredKeys.Clear();
            foreach (KeyValuePair<string, Backoff> entry in table)
            {
                DropOlderThan(entry.Value.failures, now, WindowMilliseconds(_settings.loginFailureWindowSeconds));
                if (entry.Value.failures.Count == 0 && entry.Value.blockedUntil <= now)
                    _expiredKeys.Add(entry.Key);
            }
            for (int i = 0; i < _expiredKeys.Count; ++i)
                table.Remove(_expiredKeys[i]);
            _expiredKeys.Clear();
        }

        /// <summary>How many keys the tables hold, for tests of the clean-up.</summary>
        public int TrackedKeyCount
        {
            get
            {
                lock (_gate)
                {
                    return _loginAttempts.Count + _registerAttempts.Count + _accounts.Count + _addressFailures.Count + _usernameFailures.Count;
                }
            }
        }
        #endregion
    }
}
