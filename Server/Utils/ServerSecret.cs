using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace MultiplayerARPG.MMO
{
    public enum ServerSecretCheck : byte
    {
        Valid,
        /// <summary>The peer sent no hash at all</summary>
        NoHash,
        /// <summary>The hash is not the one this secret makes: the peer has another secret, or none</summary>
        WrongHash,
        /// <summary>The hash is right, but it was made too long ago or too far in the future: the clocks differ, or it is a replay</summary>
        WrongTime,
        /// <summary>The hash is right and fresh, but this deployment's server already accepted it once: it is a replay</summary>
        Replayed,
    }

    public enum ServerSecretSource : byte
    {
        None,
        CommandLine,
        Environment,
        ConfigFile,
        Inspector,
    }

    /// <summary>
    /// The secret which every server process of one deployment shares (`serverSecret`).
    /// The central server's cluster port and the database server's port are for servers only,
    /// a peer proves that it belongs to the deployment by sending a hash which can be made only with this secret.
    /// Without a secret these ports serve peers on the same machine only (see <see cref="ServerPeerGate"/>).
    /// It proves who is connecting, it does not encrypt what is sent afterwards,
    /// so traffic between server machines still belongs on a private network.
    /// </summary>
    public static class ServerSecret
    {
        /// <summary>
        /// A hash is refused when its time is further than this from the receiver's clock,
        /// so a captured hash is useless soon after. Machines of one deployment must keep their clocks within it.
        /// </summary>
        public const int MaxClockDifferenceSeconds = 60;
        /// <summary>
        /// A shorter secret is refused at start: one captured hash, with its time and fields, lets anyone try secrets offline until the hash matches
        /// </summary>
        public const int MinimumLength = 16;
        public const int RecommendedLength = MinimumLength;
        public const string PurposeClusterRegister = "cluster-register";
        public const string PurposeDatabaseClient = "database-client";

        public static string Value { get; private set; } = string.Empty;
        public static ServerSecretSource Source { get; private set; } = ServerSecretSource.None;
        public static bool IsSet { get { return Value.Length > 0; } }
        public static long Now { get { return DateTimeOffset.UtcNow.ToUnixTimeSeconds(); } }

        public static void Set(string secret, ServerSecretSource source)
        {
            // White space around it is never meant, it comes from how the value was written (a trailing new line in a file or a variable)
            Value = string.IsNullOrWhiteSpace(secret) ? string.Empty : secret.Trim();
            Source = Value.Length > 0 ? source : ServerSecretSource.None;
        }

        /// <summary>
        /// Why servers must not start with this secret, or `NULL` if it is fine.
        /// Without a secret nothing can tell a server of this deployment from anyone else who reaches the port,
        /// not even by address (a forwarder on this machine makes every outside peer look local).
        /// </summary>
        public static string Problem(string secret)
        {
            string key = ProcessArguments.CONFIG_SERVER_SECRET;
            if (string.IsNullOrEmpty(secret))
                return $"No `{key}` is set. Every server needs one, the same on all of them: set it in Config/serverConfig.json, in the `{key}` environment variable or with -{key} (for example the output of `openssl rand -hex 32`).";
            if (secret.Length < MinimumLength)
                return $"`{key}` is only {secret.Length} characters long, the least is {MinimumLength}. Use a long random value, for example the output of `openssl rand -hex 32`.";
            return null;
        }

        /// <summary>
        /// A random value which one proof carries, so that no two proofs are the same
        /// </summary>
        public static string NewNonce()
        {
            byte[] bytes = new byte[16];
            using (RandomNumberGenerator random = RandomNumberGenerator.Create())
            {
                random.GetBytes(bytes);
            }
            StringBuilder hex = new StringBuilder(bytes.Length * 2);
            for (int i = 0; i < bytes.Length; ++i)
            {
                hex.Append(bytes[i].ToString("x2", CultureInfo.InvariantCulture));
            }
            return hex.ToString();
        }

        public static void Clear()
        {
            Set(string.Empty, ServerSecretSource.None);
        }

        /// <summary>
        /// Find the secret: command line (`-serverSecret value`), then the environment variable `serverSecret`,
        /// then the config file's value, then the inspector's.
        /// </summary>
        public static string Resolve(string[] args, string fromConfigFile, string fromInspector, out ServerSecretSource source)
        {
            string result;
            if (ConfigReader.ReadArgs(args, ProcessArguments.ARG_SERVER_SECRET, out result, string.Empty) && !string.IsNullOrEmpty(result))
            {
                source = ServerSecretSource.CommandLine;
                return result;
            }
            if (ConfigReader.ReadEnv(ProcessArguments.CONFIG_SERVER_SECRET, out result, string.Empty))
            {
                source = ServerSecretSource.Environment;
                return result;
            }
            if (!string.IsNullOrEmpty(fromConfigFile))
            {
                source = ServerSecretSource.ConfigFile;
                return fromConfigFile;
            }
            if (!string.IsNullOrEmpty(fromInspector))
            {
                source = ServerSecretSource.Inspector;
                return fromInspector;
            }
            source = ServerSecretSource.None;
            return string.Empty;
        }

        /// <summary>
        /// <see cref="Resolve"/> then <see cref="Set"/>
        /// </summary>
        public static void Apply(string[] args, string fromConfigFile, string fromInspector)
        {
            ServerSecretSource source;
            string secret = Resolve(args, fromConfigFile, fromInspector, out source);
            Set(secret, source);
        }

        /// <summary>
        /// HMAC-SHA256, keyed by the secret, over the purpose, the time and the fields, as lowercase hex.
        /// The purpose keeps a hash made for one port from being accepted at another.
        /// Fields are length-prefixed, so two different lists of fields never make the same message.
        /// </summary>
        public static string ComputeHash(string secret, string purpose, long time, params string[] fields)
        {
            StringBuilder message = new StringBuilder("OpenMMORPG|1|");
            AppendField(message, purpose);
            AppendField(message, time.ToString(CultureInfo.InvariantCulture));
            if (fields != null)
            {
                for (int i = 0; i < fields.Length; ++i)
                {
                    AppendField(message, fields[i]);
                }
            }
            byte[] hash;
            using (HMACSHA256 hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret ?? string.Empty)))
            {
                hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(message.ToString()));
            }
            StringBuilder hex = new StringBuilder(hash.Length * 2);
            for (int i = 0; i < hash.Length; ++i)
            {
                hex.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
            }
            return hex.ToString();
        }

        private static void AppendField(StringBuilder message, string field)
        {
            if (field == null)
                field = string.Empty;
            message.Append(field.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(field).Append('|');
        }

        /// <summary>
        /// Is `hash` the one which `secret` makes for this purpose, time and fields, and was it made within
        /// <see cref="MaxClockDifferenceSeconds"/> of `now`?
        /// </summary>
        public static ServerSecretCheck Check(string secret, string hash, string purpose, long time, long now, params string[] fields)
        {
            if (string.IsNullOrEmpty(hash))
                return ServerSecretCheck.NoHash;
            if (!FixedTimeEquals(hash, ComputeHash(secret, purpose, time, fields)))
                return ServerSecretCheck.WrongHash;
            if (Math.Abs(now - time) > MaxClockDifferenceSeconds)
                return ServerSecretCheck.WrongTime;
            return ServerSecretCheck.Valid;
        }

        /// <summary>
        /// Compares every character whatever the result, so how long it takes tells nothing about where two hashes differ.
        /// </summary>
        public static bool FixedTimeEquals(string a, string b)
        {
            if (a == null || b == null || a.Length != b.Length)
                return false;
            int difference = 0;
            for (int i = 0; i < a.Length; ++i)
            {
                difference |= a[i] ^ b[i];
            }
            return difference == 0;
        }
    }
}
