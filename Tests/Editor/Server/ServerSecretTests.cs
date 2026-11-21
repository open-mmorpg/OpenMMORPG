using System;
using System.Collections.Generic;
using LiteNetLib.Utils;
using MultiplayerARPG.MMO;
using NUnit.Framework;

namespace OpenMMORPG.Tests
{
    /// <summary>
    /// The server secret's hash, without any networking. The cluster and database ports let a
    /// peer in when it sends a hash that only the deployment's secret can make, and that was
    /// made within a minute of now. A peer with another secret, a hash captured earlier, or a
    /// captured registration changed to point somewhere else must all fail. Also where the
    /// secret is read from, and what a server says at start about settings left at the kit's
    /// defaults.
    /// </summary>
    [Category(TestCategories.Logic)]
    public class ServerSecretTests
    {
        private const string Secret = "correct-horse-battery-staple";
        private const string OtherSecret = "another-deployments-secret";
        private const long Noon = 1795348800;

        private string _savedSecret;
        private ServerSecretSource _savedSource;
        private string _savedEnvironment;

        [SetUp]
        public void SetUp()
        {
            _savedSecret = ServerSecret.Value;
            _savedSource = ServerSecret.Source;
            _savedEnvironment = Environment.GetEnvironmentVariable(ProcessArguments.CONFIG_SERVER_SECRET);
            Environment.SetEnvironmentVariable(ProcessArguments.CONFIG_SERVER_SECRET, null);
        }

        [TearDown]
        public void TearDown()
        {
            ServerSecret.Set(_savedSecret, _savedSource);
            Environment.SetEnvironmentVariable(ProcessArguments.CONFIG_SERVER_SECRET, _savedEnvironment);
        }

        private static CentralServerPeerInfo MapServer(string address = "10.0.0.5", int port = 8000)
        {
            return new CentralServerPeerInfo()
            {
                peerType = CentralServerPeerType.MapServer,
                networkAddress = address,
                networkPort = port,
                channelId = "eu",
                refId = "Town",
            };
        }

        /// <summary>A registration as it arrives at the central server: made with <paramref name="secret"/> at <paramref name="time"/>.</summary>
        private static RequestAppServerRegisterMessage Registration(CentralServerPeerInfo signedPeer, string secret, long time, CentralServerPeerInfo? sentPeer = null)
        {
            string hash = new RequestAppServerRegisterMessage() { peerInfo = signedPeer }.ComputeHash(secret, time);
            NetDataWriter writer = new NetDataWriter();
            (sentPeer ?? signedPeer).Serialize(writer);
            writer.PutPackedLong(time);
            writer.Put(hash);
            RequestAppServerRegisterMessage received = new RequestAppServerRegisterMessage();
            received.Deserialize(new NetDataReader(writer.CopyData()));
            return received;
        }

        [Test]
        public void TheRightSecretPasses()
        {
            string hash = ServerSecret.ComputeHash(Secret, ServerSecret.PurposeDatabaseClient, Noon);

            Assert.That(ServerSecret.Check(Secret, hash, ServerSecret.PurposeDatabaseClient, Noon, Noon), Is.EqualTo(ServerSecretCheck.Valid));
            Assert.That(hash, Has.Length.EqualTo(64), "HMAC-SHA256 as hex");
            Assert.That(hash, Does.Not.Contain(Secret));
        }

        [Test]
        public void AnotherSecretFails()
        {
            string hash = ServerSecret.ComputeHash(OtherSecret, ServerSecret.PurposeDatabaseClient, Noon);

            Assert.That(ServerSecret.Check(Secret, hash, ServerSecret.PurposeDatabaseClient, Noon, Noon), Is.EqualTo(ServerSecretCheck.WrongHash));
        }

        [Test]
        public void NoHashFails()
        {
            Assert.That(ServerSecret.Check(Secret, string.Empty, ServerSecret.PurposeDatabaseClient, Noon, Noon), Is.EqualTo(ServerSecretCheck.NoHash));
            Assert.That(ServerSecret.Check(Secret, null, ServerSecret.PurposeDatabaseClient, Noon, Noon), Is.EqualTo(ServerSecretCheck.NoHash));
        }

        [Test]
        public void AHashOlderThanAMinuteFails()
        {
            string hash = ServerSecret.ComputeHash(Secret, ServerSecret.PurposeDatabaseClient, Noon);
            int limit = ServerSecret.MaxClockDifferenceSeconds;

            Assert.That(ServerSecret.Check(Secret, hash, ServerSecret.PurposeDatabaseClient, Noon, Noon + limit), Is.EqualTo(ServerSecretCheck.Valid), "at the limit");
            Assert.That(ServerSecret.Check(Secret, hash, ServerSecret.PurposeDatabaseClient, Noon, Noon + limit + 1), Is.EqualTo(ServerSecretCheck.WrongTime), "a captured hash replayed later");
            Assert.That(ServerSecret.Check(Secret, hash, ServerSecret.PurposeDatabaseClient, Noon, Noon + 86400), Is.EqualTo(ServerSecretCheck.WrongTime));
        }

        [Test]
        public void AHashFromTooFarInTheFutureFails()
        {
            string hash = ServerSecret.ComputeHash(Secret, ServerSecret.PurposeDatabaseClient, Noon);
            int limit = ServerSecret.MaxClockDifferenceSeconds;

            Assert.That(ServerSecret.Check(Secret, hash, ServerSecret.PurposeDatabaseClient, Noon, Noon - limit), Is.EqualTo(ServerSecretCheck.Valid));
            Assert.That(ServerSecret.Check(Secret, hash, ServerSecret.PurposeDatabaseClient, Noon, Noon - limit - 1), Is.EqualTo(ServerSecretCheck.WrongTime));
        }

        [Test]
        public void TheTimeCannotBeChangedToMakeAnOldHashFresh()
        {
            string hash = ServerSecret.ComputeHash(Secret, ServerSecret.PurposeDatabaseClient, Noon);

            // The replayer sends the old hash with the present time beside it.
            Assert.That(ServerSecret.Check(Secret, hash, ServerSecret.PurposeDatabaseClient, Noon + 3600, Noon + 3600), Is.EqualTo(ServerSecretCheck.WrongHash));
        }

        [Test]
        public void AHashForOnePortIsRefusedAtTheOther()
        {
            string forTheCluster = ServerSecret.ComputeHash(Secret, ServerSecret.PurposeClusterRegister, Noon);

            Assert.That(ServerSecret.Check(Secret, forTheCluster, ServerSecret.PurposeDatabaseClient, Noon, Noon), Is.EqualTo(ServerSecretCheck.WrongHash));
        }

        [Test]
        public void FieldsCannotBeShiftedIntoEachOther()
        {
            string hash = ServerSecret.ComputeHash(Secret, "p", Noon, "ab", "c");

            Assert.That(ServerSecret.ComputeHash(Secret, "p", Noon, "ab", "c"), Is.EqualTo(hash));
            Assert.That(ServerSecret.ComputeHash(Secret, "p", Noon, "a", "bc"), Is.Not.EqualTo(hash));
            Assert.That(ServerSecret.ComputeHash(Secret, "p", Noon, "abc"), Is.Not.EqualTo(hash));
            Assert.That(ServerSecret.ComputeHash(Secret, "p", Noon, "ab", "c", null), Is.Not.EqualTo(hash));
        }

        [Test]
        public void ARegistrationMadeWithTheSecretPasses()
        {
            RequestAppServerRegisterMessage request = Registration(MapServer(), Secret, Noon);

            Assert.That(request.CheckHash(Secret, Noon + 5), Is.EqualTo(ServerSecretCheck.Valid));
        }

        [Test]
        public void ARegistrationMadeWithAnotherSecretOrNoneFails()
        {
            Assert.That(Registration(MapServer(), OtherSecret, Noon).CheckHash(Secret, Noon), Is.EqualTo(ServerSecretCheck.WrongHash));
            // No secret makes the old hash, which any build of the kit can make.
            Assert.That(Registration(MapServer(), string.Empty, Noon).CheckHash(Secret, Noon), Is.EqualTo(ServerSecretCheck.WrongHash));
        }

        [Test]
        public void AStaleRegistrationFails()
        {
            RequestAppServerRegisterMessage request = Registration(MapServer(), Secret, Noon);

            Assert.That(request.CheckHash(Secret, Noon + ServerSecret.MaxClockDifferenceSeconds + 1), Is.EqualTo(ServerSecretCheck.WrongTime));
        }

        [Test]
        public void ACapturedRegistrationCannotBePointedAtAnotherMachine()
        {
            CentralServerPeerInfo real = MapServer("10.0.0.5", 8000);

            Assert.That(Registration(real, Secret, Noon, MapServer("203.0.113.9", 8000)).CheckHash(Secret, Noon), Is.EqualTo(ServerSecretCheck.WrongHash), "another address");
            Assert.That(Registration(real, Secret, Noon, MapServer("10.0.0.5", 9999)).CheckHash(Secret, Noon), Is.EqualTo(ServerSecretCheck.WrongHash), "another port");

            CentralServerPeerInfo asLogin = real;
            asLogin.peerType = CentralServerPeerType.LoginServer;
            Assert.That(Registration(real, Secret, Noon, asLogin).CheckHash(Secret, Noon), Is.EqualTo(ServerSecretCheck.WrongHash), "another kind of server");

            CentralServerPeerInfo otherMap = real;
            otherMap.refId = "Forest";
            Assert.That(Registration(real, Secret, Noon, otherMap).CheckHash(Secret, Noon), Is.EqualTo(ServerSecretCheck.WrongHash), "another map");
        }

        [Test]
        public void WithoutASecretTheOldHashStillRegisters()
        {
            // Builds from before the secret existed, and servers that set none, keep registering with each other.
            RequestAppServerRegisterMessage request = Registration(MapServer(), string.Empty, Noon);

            Assert.That(request.hash, Is.EqualTo(ClusterServer.GetAppServerRegisterHash(CentralServerPeerType.MapServer, Noon)));
            Assert.That(request.CheckHash(string.Empty, Noon + 86400), Is.EqualTo(ServerSecretCheck.Valid), "the old hash never expired");
            Assert.That(Registration(MapServer(), Secret, Noon).CheckHash(string.Empty, Noon), Is.EqualTo(ServerSecretCheck.WrongHash), "a peer with a secret meets a server without one");
        }

        [Test]
        public void SerializeSignsWithTheConfiguredSecret()
        {
            ServerSecret.Set(Secret, ServerSecretSource.ConfigFile);
            NetDataWriter writer = new NetDataWriter();
            new RequestAppServerRegisterMessage() { peerInfo = MapServer() }.Serialize(writer);
            RequestAppServerRegisterMessage received = new RequestAppServerRegisterMessage();
            received.Deserialize(new NetDataReader(writer.CopyData()));

            Assert.That(received.ValidateHash(), Is.True);
            Assert.That(received.CheckHash(OtherSecret, ServerSecret.Now), Is.EqualTo(ServerSecretCheck.WrongHash));

            ServerSecret.Set(OtherSecret, ServerSecretSource.ConfigFile);
            Assert.That(received.ValidateHash(), Is.False);
        }

        [Test]
        public void TheSecretIsReadFromTheCommandLineThenTheEnvironmentThenTheConfigFileThenTheInspector()
        {
            string[] args = new[] { "-startMapServer", ProcessArguments.ARG_SERVER_SECRET, "from-args" };
            ServerSecretSource source;

            Assert.That(ServerSecret.Resolve(new string[0], null, "from-inspector", out source), Is.EqualTo("from-inspector"));
            Assert.That(source, Is.EqualTo(ServerSecretSource.Inspector));

            Assert.That(ServerSecret.Resolve(new string[0], "from-file", "from-inspector", out source), Is.EqualTo("from-file"));
            Assert.That(source, Is.EqualTo(ServerSecretSource.ConfigFile));

            Environment.SetEnvironmentVariable(ProcessArguments.CONFIG_SERVER_SECRET, "from-environment");
            Assert.That(ServerSecret.Resolve(new string[0], "from-file", "from-inspector", out source), Is.EqualTo("from-environment"));
            Assert.That(source, Is.EqualTo(ServerSecretSource.Environment));

            Assert.That(ServerSecret.Resolve(args, "from-file", "from-inspector", out source), Is.EqualTo("from-args"));
            Assert.That(source, Is.EqualTo(ServerSecretSource.CommandLine));
        }

        [Test]
        public void NoSecretAnywhereMeansNone()
        {
            ServerSecretSource source;

            Assert.That(ServerSecret.Resolve(null, null, null, out source), Is.Empty);
            Assert.That(source, Is.EqualTo(ServerSecretSource.None));
            Assert.That(ServerSecret.Resolve(new[] { ProcessArguments.ARG_SERVER_SECRET }, string.Empty, string.Empty, out source), Is.Empty, "the argument without a value");

            ServerSecret.Apply(null, null, null);
            Assert.That(ServerSecret.IsSet, Is.False);
            ServerSecret.Apply(null, "from-file", null);
            Assert.That(ServerSecret.IsSet, Is.True);
            Assert.That(ServerSecret.Source, Is.EqualTo(ServerSecretSource.ConfigFile));

            ServerSecret.Set("  from-a-file-with-a-new-line\n", ServerSecretSource.Environment);
            Assert.That(ServerSecret.Value, Is.EqualTo("from-a-file-with-a-new-line"));
            ServerSecret.Set("   ", ServerSecretSource.Environment);
            Assert.That(ServerSecret.IsSet, Is.False);
            Assert.That(ServerSecret.Source, Is.EqualTo(ServerSecretSource.None));
        }

        [Test]
        public void AServerWithoutASecretSaysSoAtStart()
        {
            List<string> warnings = ServerSecurityWarnings.ForServerSecret(string.Empty, ServerSecretSource.None);

            Assert.That(warnings, Has.Count.EqualTo(1));
            Assert.That(warnings[0], Does.Contain("this machine only"));
        }

        [Test]
        public void AShortSecretAndOneFromTheInspectorAreWarnedAbout()
        {
            Assert.That(ServerSecurityWarnings.ForServerSecret(Secret, ServerSecretSource.ConfigFile), Is.Empty);
            Assert.That(ServerSecurityWarnings.ForServerSecret(Secret, ServerSecretSource.Environment), Is.Empty);

            List<string> shortOne = ServerSecurityWarnings.ForServerSecret("hunter2", ServerSecretSource.ConfigFile);
            Assert.That(shortOne, Has.Count.EqualTo(1));
            Assert.That(shortOne[0], Does.Contain("7 characters"));

            List<string> fromInspector = ServerSecurityWarnings.ForServerSecret(Secret, ServerSecretSource.Inspector);
            Assert.That(fromInspector, Has.Count.EqualTo(1));
            Assert.That(fromInspector[0], Does.Contain("client build"));
        }

        [Test]
        public void TheConnectKeyAndWebApiKeyTheKitShipsWithAreWarnedAbout()
        {
            Assert.That(ServerSecurityWarnings.ForConnectKey("login", ServerSecurityWarnings.DefaultConnectKey), Does.Contain("client build"));
            Assert.That(ServerSecurityWarnings.ForConnectKey("login", "my-game-7c1"), Is.Null);
            Assert.That(ServerSecurityWarnings.ForConnectKey("login", new LiteNetLibManager.LiteNetLibTransport(ServerSecurityWarnings.DefaultConnectKey, 1, 1)), Is.Not.Null);
            Assert.That(ServerSecurityWarnings.ForConnectKey("login", new TestTransport()), Is.Null, "a transport without a connect key");

            Assert.That(ServerSecurityWarnings.ForDatabaseApiKey(ServerSecurityWarnings.DefaultDatabaseApiKey), Is.Not.Null);
            Assert.That(ServerSecurityWarnings.ForDatabaseApiKey("9f1c2e7a40b3"), Is.Null);
        }

        [Test]
        public void TheKitsFactoriesStillShipTheConnectKeyTheWarningNames()
        {
            // If upstream changes its default, the warning must follow it.
            UnityEngine.GameObject holder = new UnityEngine.GameObject(nameof(ServerSecretTests));
            try
            {
                Assert.That(holder.AddComponent<LiteNetLibManager.LiteNetLibTransportFactory>().connectKey, Is.EqualTo(ServerSecurityWarnings.DefaultConnectKey));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(holder);
            }
        }
    }
}
