using System.Collections.Generic;
using MultiplayerARPG.MMO;
using NUnit.Framework;

namespace OpenMMORPG.Tests
{
    /// <summary>
    /// The default password hashing every database uses for logins. The right password must
    /// verify and any other must not, or players are locked out or let into other accounts; the
    /// configured salt must change the stored hash, so a leaked hash list from one server does
    /// not match another's. Hashes are PBKDF2 with a random per-user salt; legacy MD5 hashes must still
    /// verify and be flagged for rehash so old accounts are not locked out. New user ids must never repeat, or two accounts would share one id.
    /// </summary>
    [Category(TestCategories.Logic)]
    public class UserLoginHashingTests : KitTestFixture
    {
        private static DefaultDatabaseUserLogin Login(string prefix = "", string postfix = "", int iterations = 0)
        {
            return new DefaultDatabaseUserLogin(new DefaultDatabaseUserLoginConfig()
            {
                PasswordSaltPrefix = prefix,
                PasswordSaltPostfix = postfix,
                PasswordHashIterations = iterations,
            });
        }

        // "secret" with the salt 00 01 .. 0f, made outside the kit
        private const string SevenRoundsOfSecret = "p3$7$AAECAwQFBgcICQoLDA0ODw$P02XRSU+mYpB6TLVMUsSiacmB7qdM03Z";
        // As stored before the rounds were a setting: 210,000 of them
        private const string FixedRoundsOfSecret = "p2$AAECAwQFBgcICQoLDA0ODw$bh1CyNbqO8E74L/vu45as7NSq2mTLFzVbvIO6tmSSMM";

        [Test]
        public void AThousandRoundsUnlessSetAndTheHashStoresItsRounds()
        {
            Assert.That(Login().Iterations, Is.EqualTo(1000));
            Assert.That(Login(iterations: -3).Iterations, Is.EqualTo(1000));
            Assert.That(Login().GetHashedPassword("secret"), Does.StartWith("p3$1000$"));
            Assert.That(Login(iterations: 250).GetHashedPassword("secret"), Does.StartWith("p3$250$"));
        }

        [Test]
        public void AHashWithOtherRoundsStillVerifiesAndIsFlaggedForRehash()
        {
            DefaultDatabaseUserLogin login = Login(iterations: 40);

            Assert.That(login.VerifyPassword("secret", SevenRoundsOfSecret), Is.True);
            Assert.That(login.VerifyPassword("Secret", SevenRoundsOfSecret), Is.False);
            Assert.That(login.NeedsRehash(SevenRoundsOfSecret), Is.True);
            Assert.That(Login(iterations: 7).NeedsRehash(SevenRoundsOfSecret), Is.False);
            Assert.That(login.VerifyPassword("secret", Login(iterations: 90).GetHashedPassword("secret")), Is.True);
        }

        [Test]
        public void AHashFromBeforeTheSettingStillVerifiesAndIsFlaggedForRehash()
        {
            DefaultDatabaseUserLogin login = Login();

            Assert.That(login.VerifyPassword("secret", FixedRoundsOfSecret), Is.True);
            Assert.That(login.NeedsRehash(FixedRoundsOfSecret), Is.True);
        }

        [Test]
        public void AMalformedHashIsRefused()
        {
            DefaultDatabaseUserLogin login = Login();
            string salt = "AAECAwQFBgcICQoLDA0ODw";
            string hash = "P02XRSU+mYpB6TLVMUsSiacmB7qdM03Z";

            Assert.That(login.VerifyPassword("secret", $"p3$0${salt}${hash}"), Is.False);
            Assert.That(login.VerifyPassword("secret", $"p3$seven${salt}${hash}"), Is.False);
            Assert.That(login.VerifyPassword("secret", $"p3$7${salt}"), Is.False);
            Assert.That(login.VerifyPassword("secret", $"p3$7${salt}$"), Is.False);
            Assert.That(login.VerifyPassword("secret", $"p3$7${salt}$not base64!"), Is.False);
            Assert.That(login.NeedsRehash($"p3$seven${salt}${hash}"), Is.True);
        }

        [Test]
        public void OnlyTheExactPasswordVerifiesAgainstItsHash()
        {
            DefaultDatabaseUserLogin login = Login("pre", "post");
            string hash = login.GetHashedPassword("secret");

            Assert.That(hash, Is.Not.EqualTo("secret").And.Not.Contains("secret"));
            Assert.That(login.VerifyPassword("secret", hash), Is.True);
            Assert.That(login.VerifyPassword("Secret", hash), Is.False);
            Assert.That(login.VerifyPassword("secret ", hash), Is.False);
            Assert.That(login.VerifyPassword("", hash), Is.False);
            Assert.That(login.VerifyPassword("secret", login.GetHashedPassword("other")), Is.False);
        }

        [Test]
        public void TheSaltChangesTheHashSoOneServersHashesDoNotVerifyOnAnother()
        {
            string unsalted = Login().GetHashedPassword("secret");
            string prefixed = Login("pepper").GetHashedPassword("secret");
            string postfixed = Login(postfix: "pepper").GetHashedPassword("secret");

            Assert.That(new[] { unsalted, prefixed, postfixed }, Is.Unique);
            Assert.That(Login("other").VerifyPassword("secret", prefixed), Is.False);
            Assert.That(Login().VerifyPassword("secret", prefixed), Is.False);
            Assert.That(Login("pepper").VerifyPassword("secret", prefixed), Is.True, "the same salt verifies");
        }

        [Test]
        public void SamePasswordGetsADifferentHashEachTimeAndFitsTheMySqlColumn()
        {
            DefaultDatabaseUserLogin login = Login();
            string a = login.GetHashedPassword("secret");
            string b = login.GetHashedPassword("secret");

            Assert.That(a, Is.Not.EqualTo(b));
            Assert.That(a.Length, Is.LessThanOrEqualTo(72));
            Assert.That(login.NeedsRehash(a), Is.False);
        }

        [Test]
        public void LegacyMd5HashesStillVerifyAndAreFlaggedForRehash()
        {
            const string md5OfSecret = "5ebe2294ecd0e0f08eab7690d2a6ee69";
            DefaultDatabaseUserLogin login = Login();

            Assert.That(login.VerifyPassword("secret", md5OfSecret), Is.True);
            Assert.That(login.VerifyPassword("other", md5OfSecret), Is.False);
            Assert.That(login.NeedsRehash(md5OfSecret), Is.True);
            Assert.That(login.VerifyPassword("secret", "p1$garbage"), Is.False);
        }

        [Test]
        public void NewUserIdsNeverRepeat()
        {
            DefaultDatabaseUserLogin login = Login();
            HashSet<string> ids = new HashSet<string>();
            for (int i = 0; i < 1000; ++i)
            {
                string id = login.GenerateNewId();
                Assert.That(id, Is.Not.Empty);
                Assert.That(ids.Add(id), Is.True, $"id {id} repeated");
            }
        }
    }
}
