#if (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
using System.Collections.Generic;
using System.Net;
using System.Text.RegularExpressions;
using LiteNetLib.Utils;
using LiteNetLibManager;
using MultiplayerARPG;
using MultiplayerARPG.MMO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace OpenMMORPG.Tests
{
    /// <summary>
    /// Who the central server's cluster port serves. It is for the deployment's own servers:
    /// through it players are kicked, chat is relayed and map servers are started, so a stranger
    /// who reaches the port must get nothing. With a server secret, a connection is served only
    /// after it registered with a hash made with that secret. Without one, only connections from
    /// the same machine are served. Packets go through OnServerReceive, as the transport hands
    /// them over, on a never-started ClusterServer with a <see cref="TestTransport"/>.
    /// </summary>
    [Category(TestCategories.Logic)]
    public class ClusterServerSecretTests : KitTestFixture
    {
        private const string Secret = "correct-horse-battery-staple";
        private const long MapConnection = 1;
        private const long Stranger = 2;
        private const long LoginConnection = 3;

        private ClusterServer _cluster;
        private TestTransport _transport;
        private string _savedSecret;
        private ServerSecretSource _savedSource;
        private uint _requestId;

        [SetUp]
        public void SetUp()
        {
            TestLogger.Use(Objects);
            _savedSecret = ServerSecret.Value;
            _savedSource = ServerSecret.Source;
            ServerSecret.Set(Secret, ServerSecretSource.ConfigFile);

            CentralNetworkManager central = Objects.NewGameObject(nameof(ClusterServerSecretTests)).AddComponent<CentralNetworkManager>();
            central.channels = new List<ChannelData>() { new ChannelData() { id = "eu", title = "Europe", maxConnections = 10 } };
            _cluster = new ClusterServer(central);
            _transport = new TestTransport();
            _cluster.Transport = _transport;
        }

        [TearDown]
        public void TearDown()
        {
            ServerSecret.Set(_savedSecret, _savedSource);
        }

        private static CentralServerPeerInfo Peer(CentralServerPeerType type, string refId = "", int port = 0)
        {
            return new CentralServerPeerInfo() { peerType = type, networkAddress = "10.0.0.1", networkPort = port, channelId = "eu", refId = refId };
        }

        private void Connect(long connectionId)
        {
            _cluster.OnServerReceive(TestTransport.Connect(connectionId));
        }

        /// <summary>Sends a register request made with <paramref name="secret"/> and returns the answer's code.</summary>
        private AckResponseCode Register(long connectionId, CentralServerPeerInfo peer, string secret, long? time = null, string nonce = null)
        {
            long madeAt = time ?? ServerSecret.Now;
            nonce = nonce ?? ServerSecret.NewNonce();
            string hash = new RequestAppServerRegisterMessage() { peerInfo = peer }.ComputeHash(secret, madeAt, nonce);
            NetDataWriter writer = new NetDataWriter();
            writer.PutPackedUShort(MMOMessageTypes.Request);
            writer.PutPackedUShort(MMORequestTypes.AppServerRegister);
            writer.PutPackedUInt(++_requestId);
            peer.Serialize(writer);
            writer.PutPackedLong(madeAt);
            writer.Put(hash);
            writer.Put(nonce);
            int before = _transport.ResponsesTo(connectionId, MMOMessageTypes.Response).Count;
            _cluster.OnServerReceive(TestTransport.Data(connectionId, writer));
            List<AckResponseCode> responses = _transport.ResponsesTo(connectionId, MMOMessageTypes.Response);
            Assert.That(responses, Has.Count.EqualTo(before + 1), "a register request is always answered");
            return responses[responses.Count - 1];
        }

        private void Kick(long fromConnection, string userId)
        {
            _cluster.OnServerReceive(TestTransport.Message(fromConnection, MMOMessageTypes.KickUser, (writer) =>
            {
                writer.Put(userId);
                writer.PutPackedUShort((ushort)UITextKeys.UI_ERROR_KICKED_FROM_SERVER);
            }));
        }

        private void ReportOnline(long fromConnection, string userId)
        {
            UpdateUserCharacterMessage message = new UpdateUserCharacterMessage()
            {
                type = UpdateUserCharacterMessage.UpdateType.Online,
                character = new SocialCharacterData() { userId = userId, id = userId + "-character", characterName = userId },
            };
            _cluster.OnServerReceive(TestTransport.Message(fromConnection, MMOMessageTypes.UpdateMapUser, (writer) => writer.PutValue(message)));
        }

        private bool IsTrackedOnline(string userId)
        {
            return TestReflection.GetField<Dictionary<string, long>>(_cluster, "_mapUserConnectionIds").ContainsKey(userId);
        }

        [Test]
        public void AServerWithTheSecretRegistersAndIsServed()
        {
            Connect(MapConnection);
            Connect(LoginConnection);

            Assert.That(Register(MapConnection, Peer(CentralServerPeerType.MapServer, "Town", 8000), Secret), Is.EqualTo(AckResponseCode.Success));
            Assert.That(Register(LoginConnection, Peer(CentralServerPeerType.LoginServer), Secret), Is.EqualTo(AckResponseCode.Success));
            Assert.That(_cluster.MapServerPeers.Keys, Is.EquivalentTo(new[] { MapConnection }));
            Assert.That(_cluster.LoginServerPeers.Keys, Is.EquivalentTo(new[] { LoginConnection }));

            // The login server kicks a player: every map server is told.
            Kick(LoginConnection, "alice");
            Assert.That(_transport.MessagesTo(MapConnection, MMOMessageTypes.KickUser), Is.EqualTo(1));

            ReportOnline(MapConnection, "bob");
            Assert.That(IsTrackedOnline("bob"), Is.True);
            Assert.That(_transport.Disconnected, Is.Empty);
        }

        [Test]
        public void AServerWithAnotherSecretOrNoneIsNotRegistered()
        {
            // What the server logs about each refusal, in order. An error nobody expected fails the test
            LogAssert.Expect(LogType.Error, new Regex("Register Server Failed.*it does not have this server's `serverSecret`"));
            LogAssert.Expect(LogType.Error, new Regex("Register Server Failed.*it does not have this server's `serverSecret`"));
            Connect(Stranger);

            Assert.That(Register(Stranger, Peer(CentralServerPeerType.MapServer, "Town", 8000), "another-deployments-secret"), Is.EqualTo(AckResponseCode.Error));
            // The hash every build of the kit can make, which used to be enough.
            Assert.That(Register(Stranger, Peer(CentralServerPeerType.LoginServer), string.Empty), Is.EqualTo(AckResponseCode.Error));

            Assert.That(_cluster.MapServerPeers, Is.Empty);
            Assert.That(_cluster.LoginServerPeers, Is.Empty);
            Assert.That(_cluster.Gate.IsAuthorized(Stranger), Is.False);
        }

        [Test]
        public void ARegistrationReplayedLaterIsNotRegistered()
        {
            // What the server logs about each refusal, in order. An error nobody expected fails the test
            LogAssert.Expect(LogType.Error, new Regex("Register Server Failed.*its clock differs"));
            Connect(Stranger);
            long captured = ServerSecret.Now - ServerSecret.MaxClockDifferenceSeconds - 5;

            Assert.That(Register(Stranger, Peer(CentralServerPeerType.MapServer, "Town", 8000), Secret, captured), Is.EqualTo(AckResponseCode.Error));

            Assert.That(_cluster.MapServerPeers, Is.Empty);
            Assert.That(_cluster.Gate.IsAuthorized(Stranger), Is.False);
        }

        [Test]
        public void ARegistrationWhichWasAcceptedAlreadyIsRefusedWhenItIsSentAgain()
        {
            // What the server logs about each refusal, in order. An error nobody expected fails the test
            LogAssert.Expect(LogType.Error, new Regex("Register Server Failed.*this exact registration was accepted before"));
            Connect(MapConnection);
            Connect(Stranger);
            long madeAt = ServerSecret.Now;
            string nonce = ServerSecret.NewNonce();
            CentralServerPeerInfo peer = Peer(CentralServerPeerType.MapServer, "Town", 8000);

            Assert.That(Register(MapConnection, peer, Secret, madeAt, nonce), Is.EqualTo(AckResponseCode.Success));
            // A copy which someone captured on the network
            Assert.That(Register(Stranger, peer, Secret, madeAt, nonce), Is.EqualTo(AckResponseCode.Error));

            Assert.That(_cluster.Gate.IsAuthorized(Stranger), Is.False);
        }

        [Test]
        public void AConnectionThatDidNotRegisterCannotKickPlayersOrReportUsers()
        {
            // What the server logs about each refusal, in order. An error nobody expected fails the test
            LogAssert.Expect(LogType.Error, new Regex("Ignoring connection 2 on the cluster port"));
            Connect(MapConnection);
            Register(MapConnection, Peer(CentralServerPeerType.MapServer, "Town", 8000), Secret);
            Connect(Stranger);

            Kick(Stranger, "alice");
            ReportOnline(Stranger, "mallory");
            _cluster.OnServerReceive(TestTransport.Message(Stranger, MMOMessageTypes.Chat, (writer) => writer.PutValue(new ChatMessage() { message = "free gold" })));

            Assert.That(_transport.MessagesTo(MapConnection, MMOMessageTypes.KickUser), Is.Zero);
            Assert.That(_transport.MessagesTo(MapConnection, MMOMessageTypes.Chat), Is.Zero);
            Assert.That(_transport.MessagesTo(MapConnection, MMOMessageTypes.UpdateMapUser), Is.Zero);
            Assert.That(IsTrackedOnline("mallory"), Is.False);
        }

        [Test]
        public void AConnectionThatDidNotRegisterGetsNoAnswerToItsRequests()
        {
            // What the server logs about each refusal, in order. An error nobody expected fails the test
            LogAssert.Expect(LogType.Error, new Regex("Ignoring connection 2 on the cluster port"));
            Connect(Stranger);

            // Each of these is answered for a registered server.
            _cluster.OnServerReceive(TestTransport.Request(Stranger, MMOMessageTypes.Request, MMORequestTypes.SpawnMap, 1, new RequestSpawnMapMessage() { channelId = "eu", mapName = "Town" }));
            _cluster.OnServerReceive(TestTransport.Request(Stranger, MMOMessageTypes.Request, MMORequestTypes.AppServerAddress, 2, new RequestAppServerAddressMessage() { peerType = CentralServerPeerType.LoginServer }));
            _cluster.OnServerReceive(TestTransport.Request(Stranger, MMOMessageTypes.Request, MMORequestTypes.UserCount, 3, EmptyMessage.Value));

            Assert.That(_transport.ServerSent, Is.Empty);
        }

        [Test]
        public void ARegisteredServerGetsAnswers()
        {
            Connect(LoginConnection);
            Register(LoginConnection, Peer(CentralServerPeerType.LoginServer), Secret);

            _cluster.OnServerReceive(TestTransport.Request(LoginConnection, MMOMessageTypes.Request, MMORequestTypes.UserCount, 7, EmptyMessage.Value));

            Assert.That(_transport.ResponsesTo(LoginConnection, MMOMessageTypes.Response), Is.EqualTo(new[] { AckResponseCode.Success, AckResponseCode.Success }));
        }

        [Test]
        public void ARefusedRegistrationLeavesTheConnectionUnserved()
        {
            // What the server logs about each refusal, in order. An error nobody expected fails the test
            LogAssert.Expect(LogType.Error, new Regex("Register Server Failed.*it does not have this server's `serverSecret`"));
            LogAssert.Expect(LogType.Error, new Regex("Ignoring connection 2 on the cluster port"));
            Connect(MapConnection);
            Register(MapConnection, Peer(CentralServerPeerType.MapServer, "Town", 8000), Secret);
            Connect(Stranger);
            Register(Stranger, Peer(CentralServerPeerType.LoginServer), "another-deployments-secret");

            Kick(Stranger, "alice");

            Assert.That(_transport.MessagesTo(MapConnection, MMOMessageTypes.KickUser), Is.Zero);
        }

        [Test]
        public void AServerIsNoLongerServedAfterItDisconnects()
        {
            // What the server logs about each refusal, in order. An error nobody expected fails the test
            LogAssert.Expect(LogType.Error, new Regex("Ignoring connection 3 on the cluster port"));
            Connect(MapConnection);
            Register(MapConnection, Peer(CentralServerPeerType.MapServer, "Town", 8000), Secret);
            Connect(LoginConnection);
            Register(LoginConnection, Peer(CentralServerPeerType.LoginServer), Secret);

            _cluster.OnServerReceive(TestTransport.Disconnect(LoginConnection));
            // The transport gives the same connection ID to whoever connects next.
            Connect(LoginConnection);
            Kick(LoginConnection, "alice");

            Assert.That(_cluster.Gate.IsAuthorized(LoginConnection), Is.False);
            Assert.That(_transport.MessagesTo(MapConnection, MMOMessageTypes.KickUser), Is.Zero);
        }

        [Test]
        public void AConnectionThatNeverRegistersIsDroppedAfterTheTimeout()
        {
            ServerPeerGate gate = new ServerPeerGate("test", "cluster");
            gate.OnPeerConnected(Stranger, IPAddress.Parse("203.0.113.9"), 1000);
            gate.OnPeerConnected(MapConnection, IPAddress.Parse("10.0.0.5"), 1000);
            gate.Authorize(MapConnection);
            List<long> timedOut = new List<long>();

            gate.TakeTimedOut(1000 + ServerPeerGate.AuthorizeTimeoutSeconds * 1000, timedOut);
            Assert.That(timedOut, Is.Empty, "not yet");

            gate.TakeTimedOut(1001 + ServerPeerGate.AuthorizeTimeoutSeconds * 1000, timedOut);
            Assert.That(timedOut, Is.EqualTo(new[] { Stranger }), "the registered one stays");

            timedOut.Clear();
            gate.TakeTimedOut(long.MaxValue / 2, timedOut);
            Assert.That(timedOut, Is.Empty, "reported once");
        }

        [Test]
        public void WithoutASecretOnlyThisMachineIsServed()
        {
            // What the server logs about each refusal, in order. An error nobody expected fails the test
            LogAssert.Expect(LogType.Error, new Regex("Refused connection 4 from 203\\.0\\.113\\.9 to the cluster port"));
            LogAssert.Expect(LogType.Error, new Regex("Refused connection 5 from 192\\.168\\.1\\.20 to the cluster port"));
            LogAssert.Expect(LogType.Error, new Regex("Refused connection 6 from an unknown address to the cluster port"));
            ServerSecret.Clear();
            ServerPeerGate gate = _cluster.Gate;

            Assert.That(gate.OnPeerConnected(1, IPAddress.Loopback, 0), Is.True);
            Assert.That(gate.OnPeerConnected(2, IPAddress.IPv6Loopback, 0), Is.True);
            Assert.That(gate.OnPeerConnected(3, IPAddress.Loopback.MapToIPv6(), 0), Is.True, "IPv4 loopback seen through a dual-stack socket");
            Assert.That(gate.IsAuthorized(1) && gate.IsAuthorized(2) && gate.IsAuthorized(3), Is.True, "served at once, there is nothing to prove");

            Assert.That(gate.OnPeerConnected(4, IPAddress.Parse("203.0.113.9"), 0), Is.False, "the internet");
            Assert.That(gate.OnPeerConnected(5, IPAddress.Parse("192.168.1.20"), 0), Is.False, "another machine on the LAN");
            Assert.That(gate.OnPeerConnected(6, null, 0), Is.False, "a transport that cannot tell where a peer is");
            Assert.That(gate.IsAuthorized(4) || gate.IsAuthorized(5) || gate.IsAuthorized(6), Is.False);
        }

        [Test]
        public void WithoutASecretAStrangerIsDisconnectedAndItsRegistrationIgnored()
        {
            // What the server logs about each refusal, in order. An error nobody expected fails the test
            LogAssert.Expect(LogType.Error, new Regex("Refused connection 2 from an unknown address to the cluster port"));
            ServerSecret.Clear();

            // The test transport cannot tell where a peer is, which is refused like a far address.
            Connect(Stranger);
            Assert.That(_transport.Disconnected, Is.EqualTo(new[] { Stranger }));

            // Its packets can already be waiting behind the connect event. The old hash is valid without a secret.
            NetDataWriter writer = new NetDataWriter();
            writer.PutPackedUShort(MMOMessageTypes.Request);
            writer.PutPackedUShort(MMORequestTypes.AppServerRegister);
            writer.PutPackedUInt(1);
            new RequestAppServerRegisterMessage() { peerInfo = Peer(CentralServerPeerType.MapServer, "Town", 8000) }.Serialize(writer);
            _cluster.OnServerReceive(TestTransport.Data(Stranger, writer));

            Assert.That(_cluster.MapServerPeers, Is.Empty);
            Assert.That(_transport.ServerSent, Is.Empty);
        }

        [Test]
        public void WithoutASecretAServerOnThisMachineRegistersWithTheOldHash()
        {
            ServerSecret.Clear();
            Assert.That(_cluster.Gate.OnPeerConnected(MapConnection, IPAddress.Loopback, 0), Is.True);

            Assert.That(Register(MapConnection, Peer(CentralServerPeerType.MapServer, "Town", 8000), string.Empty), Is.EqualTo(AckResponseCode.Success));
            Assert.That(_cluster.MapServerPeers.Keys, Is.EquivalentTo(new[] { MapConnection }));

            ReportOnline(MapConnection, "bob");
            Assert.That(IsTrackedOnline("bob"), Is.True);
        }

        [Test]
        public void TheGateFindsTheRegisterRequestWithoutMovingTheReader()
        {
            NetDataWriter writer = new NetDataWriter();
            writer.PutPackedUShort(MMOMessageTypes.Request);
            writer.PutPackedUShort(MMORequestTypes.AppServerRegister);
            writer.PutPackedUInt(9);
            NetDataReader reader = new NetDataReader(writer.CopyData());

            Assert.That(ServerPeerGate.IsRequest(reader, MMOMessageTypes.Request, MMORequestTypes.AppServerRegister), Is.True);
            Assert.That(reader.Position, Is.Zero);
            Assert.That(ServerPeerGate.IsRequest(reader, MMOMessageTypes.Request, MMORequestTypes.SpawnMap), Is.False);
            Assert.That(ServerPeerGate.IsRequest(reader, MMOMessageTypes.Response, MMORequestTypes.AppServerRegister), Is.False);
            Assert.That(ServerPeerGate.IsRequest(new NetDataReader(new byte[0]), MMOMessageTypes.Request, MMORequestTypes.AppServerRegister), Is.False, "an empty packet");
            Assert.That(reader.Position, Is.Zero);
        }
    }
}
#endif
