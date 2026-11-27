using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace MultiplayerARPG.MMO
{
    /// <summary>
    /// New passwords are stored as "p3$rounds$salt$hash": PBKDF2-HMAC-SHA512 with a random per-user
    /// salt (the optional prefix/postfix settings act as a server-wide pepper). The number of rounds
    /// is a setting, <see cref="Iterations"/>, and is stored with each hash, so a hash made with
    /// another number still verifies. Hashes from before the setting verify too: "p2$" (210,000
    /// rounds), "p1$" (100,000) and unsalted MD5. <see cref="NeedsRehash"/> flags every hash that is
    /// not in the current form with the current number of rounds, so the database replaces it
    /// after the next right password.
    /// </summary>
    public class DefaultDatabaseUserLogin : IDatabaseUserLogin
    {
        /// <summary>
        /// The rounds of a new hash unless a server sets its own (`passwordHashIterations`).
        /// Every login and every new account costs the server one hash, and the cost grows with the
        /// rounds: 1,000 take about a third of a millisecond under .NET and about 18 ms under
        /// Unity's Mono, which computes SHA-512 in C#. More rounds make a leaked database slower
        /// to guess passwords from, by the same factor. OWASP's figure for this hash is 210,000:
        /// about 70 ms under .NET, close to 4 seconds under Mono.
        /// </summary>
        public const int DefaultIterations = 1000;
        /// <summary>A stored hash that claims more rounds than this is refused, not computed.</summary>
        public const int MaxIterations = 10000000;

        private const string Version = "p3$";
        // Before the rounds were a setting they were fixed, and not stored
        private const string FixedVersion = "p2$";
        private const int FixedIterations = 210000;
        private const string LegacyVersion = "p1$";
        private const int LegacyIterations = 100000;
        private const int SaltSize = 16;
        // 24 bytes are 32 characters: with the rounds in front the whole hash stays inside the
        // MySQL varchar(72) column
        private const int HashSize = 24;
        private const int MinStoredHashSize = 16;
        private const int MaxStoredHashSize = 64;

        private string _passwordSaltPrefix = string.Empty;
        private string _passwordSaltPostfix = string.Empty;
        private int _iterations = DefaultIterations;

        public DefaultDatabaseUserLogin(DefaultDatabaseUserLoginConfig config)
        {
            _passwordSaltPrefix = config.PasswordSaltPrefix;
            _passwordSaltPostfix = config.PasswordSaltPostfix;
            Iterations = config.PasswordHashIterations;
        }

        /// <summary>
        /// The rounds of a new hash. Zero or less means <see cref="DefaultIterations"/>. Changing it
        /// leaves the stored hashes valid; each is replaced when its account next logs in.
        /// </summary>
        public int Iterations
        {
            get { return _iterations; }
            set { _iterations = value <= 0 ? DefaultIterations : System.Math.Min(value, MaxIterations); }
        }

        public string GenerateNewId()
        {
            return System.Guid.NewGuid().ToString("N");
        }

        public string GetHashedPassword(string password)
        {
            byte[] salt = new byte[SaltSize];
            using (RandomNumberGenerator rng = RandomNumberGenerator.Create())
                rng.GetBytes(salt);
            int iterations = _iterations;
            return Version + iterations.ToString(CultureInfo.InvariantCulture) + "$" + ToB64(salt) + "$" + ToB64(Pbkdf2(password, salt, iterations, HashSize));
        }

        public bool VerifyPassword(string password, string hashedPassword)
        {
            if (string.IsNullOrEmpty(hashedPassword))
                return false;
            byte[] actual;
            byte[] expected;
            if (IsSalted(hashedPassword))
            {
                if (!TryRead(hashedPassword, out int iterations, out byte[] salt, out expected))
                    return false;
                actual = Pbkdf2(password, salt, iterations, expected.Length);
            }
            else
            {
                actual = Encoding.UTF8.GetBytes(GetMD5(password));
                expected = Encoding.UTF8.GetBytes(hashedPassword.ToLowerInvariant());
            }
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }

        public bool NeedsRehash(string hashedPassword)
        {
            if (string.IsNullOrEmpty(hashedPassword) || !hashedPassword.StartsWith(Version, System.StringComparison.Ordinal))
                return true;
            return !TryRead(hashedPassword, out int iterations, out _, out _) || iterations != _iterations;
        }

        private static bool IsSalted(string hashedPassword)
        {
            return hashedPassword.StartsWith(Version, System.StringComparison.Ordinal) ||
                hashedPassword.StartsWith(FixedVersion, System.StringComparison.Ordinal) ||
                hashedPassword.StartsWith(LegacyVersion, System.StringComparison.Ordinal);
        }

        /// <summary>Takes a salted hash apart. False when it is not one this class could have written.</summary>
        private static bool TryRead(string hashedPassword, out int iterations, out byte[] salt, out byte[] hash)
        {
            iterations = 0;
            salt = null;
            hash = null;
            string[] parts = hashedPassword.Substring(Version.Length).Split('$');
            int saltIndex = 0;
            if (hashedPassword.StartsWith(Version, System.StringComparison.Ordinal))
            {
                if (parts.Length != 3 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out iterations))
                    return false;
                saltIndex = 1;
            }
            else
            {
                if (parts.Length != 2)
                    return false;
                iterations = hashedPassword.StartsWith(FixedVersion, System.StringComparison.Ordinal) ? FixedIterations : LegacyIterations;
            }
            if (iterations < 1 || iterations > MaxIterations)
                return false;
            try
            {
                salt = FromB64(parts[saltIndex]);
                hash = FromB64(parts[saltIndex + 1]);
            }
            catch (System.FormatException)
            {
                return false;
            }
            return salt.Length > 0 && hash.Length >= MinStoredHashSize && hash.Length <= MaxStoredHashSize;
        }

        private byte[] Pbkdf2(string password, byte[] salt, int iterations, int size)
        {
            using (Rfc2898DeriveBytes kdf = new Rfc2898DeriveBytes(_passwordSaltPrefix + password + _passwordSaltPostfix, salt, iterations, HashAlgorithmName.SHA512))
                return kdf.GetBytes(size);
        }

        // Unpadded base64, to keep the whole hash short
        private static string ToB64(byte[] data) => System.Convert.ToBase64String(data).TrimEnd('=');

        private static byte[] FromB64(string text) => System.Convert.FromBase64String(text + new string('=', (4 - text.Length % 4) % 4));

        private string GetMD5(string text)
        {
            if (!string.IsNullOrEmpty(_passwordSaltPrefix))
                text = _passwordSaltPrefix + text;
            if (!string.IsNullOrEmpty(_passwordSaltPostfix))
                text = text + _passwordSaltPostfix;

            // byte array representation of that string
            byte[] encodedPassword = new UTF8Encoding().GetBytes(text);

            // need MD5 to calculate the hash
            byte[] hash = ((HashAlgorithm)CryptoConfig.CreateFromName("MD5")).ComputeHash(encodedPassword);

            // string representation (similar to UNIX format)
            return System.BitConverter.ToString(hash).Replace("-", string.Empty).ToLower();
        }
    }
}
