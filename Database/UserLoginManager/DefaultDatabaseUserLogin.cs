using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace MultiplayerARPG.MMO
{
    /// <summary>
    /// New passwords are stored as "p3$rounds$salt$hash": PBKDF2-HMAC-SHA512 with a random per-user salt (the optional
    /// prefix/postfix settings act as a server-wide pepper). The rounds are a setting and are stored with each hash.
    /// Older hashes still verify ("p2$" at 210k rounds, "p1$" at 100k, unsalted MD5); <see cref="NeedsRehash"/> flags
    /// every hash that is not "p3$" with the current rounds so the database upgrades it.
    /// </summary>
    public class DefaultDatabaseUserLogin : IDatabaseUserLogin
    {
        private string _passwordSaltPrefix = string.Empty;
        private string _passwordSaltPostfix = string.Empty;

        public DefaultDatabaseUserLogin(DefaultDatabaseUserLoginConfig config)
        {
            _passwordSaltPrefix = config.PasswordSaltPrefix;
            _passwordSaltPostfix = config.PasswordSaltPostfix;
            if (config.PasswordHashIterations > 0)
                Iterations = config.PasswordHashIterations;
        }

        public string GenerateNewId()
        {
            return System.Guid.NewGuid().ToString("N");
        }

        // 210k rounds (OWASP) take close to 4 seconds on Unity's Mono, which computes SHA-512 in C#
        public const int DefaultIterations = 1000;
        private const string Version = "p3$";
        private const string FixedVersion = "p2$";
        private const int FixedIterations = 210000;
        private const string LegacyVersion = "p1$";
        private const int LegacyIterations = 100000;
        private const int SaltSize = 16;
        // 32 chars: with the rounds in front the whole hash still fits the MySQL varchar(72) column
        private const int HashSize = 24;

        /// <summary>Rounds of a new hash.</summary>
        public int Iterations { get; set; } = DefaultIterations;

        public string GetHashedPassword(string password)
        {
            byte[] salt = new byte[SaltSize];
            using (RandomNumberGenerator rng = RandomNumberGenerator.Create())
                rng.GetBytes(salt);
            int iterations = Iterations;
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
            return !hashedPassword.StartsWith(Version, System.StringComparison.Ordinal) ||
                !TryRead(hashedPassword, out int iterations, out _, out _) || iterations != Iterations;
        }

        private static bool IsSalted(string hashedPassword)
        {
            return hashedPassword.StartsWith(Version, System.StringComparison.Ordinal) ||
                hashedPassword.StartsWith(FixedVersion, System.StringComparison.Ordinal) ||
                hashedPassword.StartsWith(LegacyVersion, System.StringComparison.Ordinal);
        }

        private static bool TryRead(string hashedPassword, out int iterations, out byte[] salt, out byte[] hash)
        {
            iterations = 0;
            salt = null;
            hash = null;
            string[] parts = hashedPassword.Substring(Version.Length).Split('$');
            int saltIndex = 0;
            if (hashedPassword.StartsWith(Version, System.StringComparison.Ordinal))
            {
                if (parts.Length != 3 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out iterations) || iterations < 1)
                    return false;
                saltIndex = 1;
            }
            else
            {
                if (parts.Length != 2)
                    return false;
                iterations = hashedPassword.StartsWith(FixedVersion, System.StringComparison.Ordinal) ? FixedIterations : LegacyIterations;
            }
            try
            {
                salt = FromB64(parts[saltIndex]);
                hash = FromB64(parts[saltIndex + 1]);
            }
            catch (System.FormatException)
            {
                return false;
            }
            return salt.Length >= 8 && hash.Length > 0;
        }

        private byte[] Pbkdf2(string password, byte[] salt, int iterations, int size)
        {
            using (Rfc2898DeriveBytes kdf = new Rfc2898DeriveBytes(_passwordSaltPrefix + password + _passwordSaltPostfix, salt, iterations, HashAlgorithmName.SHA512))
                return kdf.GetBytes(size);
        }

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
