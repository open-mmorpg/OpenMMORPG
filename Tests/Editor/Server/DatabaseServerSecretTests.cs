// The database server and its SQLite database compile for standalone server builds only.
#if (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
using System.Collections.Generic;
using System.Net;
using System.Text.RegularExpressions;
using LiteNetLib.Utils;
using LiteNetLibManager;
using MultiplayerARPG.MMO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace OpenMMORPG.Tests
{
    /// <summary>
    /// Who the database server's port serves. Every account, character and gold amount can be
    /// read and changed through it, so only the deployment's own servers may use it. With a
    /// server secret, a connection's requests are read only after it proved the secret; without
    /// one, only connections from the same machine are served. The gate sits in front of every
    /// handler, so one an addon registers is covered too. Packets go through the server's
    /// OnServerReceive, as the transport hands them over, with a <see cref="TestTransport"/>.
    /// </summary>
    [Category(TestCategories.Harnessed)]
    public class DatabaseServerSecretTests : KitTestFixture
    {
        private const string Secret = "correct-horse-battery-staple";
        private const long Server = 1;
        private const long Stranger = 2;
        private const ushort AddonRequest = 59000;

        private TestDatabaseServer _database;
        private DatabaseServerHandler _handler;
        private TestTransport _transport;
        private string _savedSecret;
        private ServerSecretSource _savedSource;
        private uint _requestId;
        private int _addonRequestsHandled;

        [SetUp]
        public void SetUp()
        {
            TestLogger.Use(Objects);
            _savedSecret = ServerSecret.Value;
            _savedSource = ServerSecret.Source;
            ServerSecret.Set(Secret, ServerSecretSource.ConfigFile);

            _database = TestDatabaseServer.Create(Objects);
            // What Awake does in a build: creates the client and server sides and registers the handlers.
            TestReflection.Invoke(_database.Manager, "InitTransportAndHandlers");
            _handler = _database.Manager.Server as DatabaseServerHandler;
            Assert.That(_handler, Is.Not.Null, "the database server's handlers sit behind the gate");
            _transport = new TestTransport();
            _handler.Transport = _transport;
        }

        [TearDown]
        public void TearDown()
        {
            ServerSecret.Set(_savedSecret, _savedSource);
        }

        private void Connect(long connectionId)
        {
            _handler.OnServerReceive(TestTransport.Connect(connectionId));
        }

        private List<AckResponseCode> Responses(long connectionId)
        {
            return _transport.ResponsesTo(connectionId, MMOMessageTypes.Response);
        }

        /// <summary>Sends the proof of the secret and returns the answer's code.</summary>
        private AckResponseCode Authenticate(long connectionId, string secret, long? time = null)
        {
            int before = Responses(connectionId).Count;
            _handler.OnServerReceive(TestTransport.Request(connectionId, MMOMessageTypes.Request, DatabaseRequestTypes.ServerAuthenticate, ++_requestId,
                DbServerAuthenticateMessage.Create(secret, time ?? ServerSecret.Now)));
            List<AckResponseCode> responses = Responses(connectionId);
            Assert.That(responses, Has.Count.EqualTo(before + 1), "the proof is always answered");
            return responses[responses.Count - 1];
        }

        /// <summary>Asks the database whether a username exists, and returns the answers so far.</summary>
        private List<AckResponseCode> AskForAUsername(long connectionId)
        {
            DbRequestMessage<FindUsernameReq> request = new DbRequestMessage<FindUsernameReq>()
            {
                RequestTimeUtc = ServerSecret.Now,
                Data = new FindUsernameReq() { Username = "alice" },
            };
            _handler.OnServerReceive(TestTransport.Request(connectionId, MMOMessageTypes.Request, DatabaseRequestTypes.FindUsername, ++_requestId, request));
            return Responses(connectionId);
        }

        [Test]
        public void AServerWithTheSecretIsServed()
        {
            Connect(Server);

            Assert.That(Authenticate(Server, Secret), Is.EqualTo(AckResponseCode.Success));
            Assert.That(AskForAUsername(Server), Is.EqualTo(new[] { AckResponseCode.Success, AckResponseCode.Success }));
            Assert.That(_transport.Disconnected, Is.Empty);
        }

        [Test]
        public void ARequestBeforeTheProofIsNotRead()
        {
            // What the server logs about each refusal, in order. An error nobody expected fails the test
            LogAssert.Expect(LogType.Error, new Regex("Ignoring connection 2 on the database port"));
            Connect(Stranger);

            Assert.That(AskForAUsername(Stranger), Is.Empty);
            Assert.That(_transport.ServerSent, Is.Empty);
        }

        [Test]
        public void AServerWithAnotherSecretOrNoneIsRefused()
        {
            // What the server logs about each refusal, in order. An error nobody expected fails the test
            LogAssert.Expect(LogType.Error, new Regex("Refused connection 2: it does not have this server's `serverSecret`"));
            LogAssert.Expect(LogType.Error, new Regex("Refused connection 2: it does not have this server's `serverSecret`"));
            LogAssert.Expect(LogType.Error, new Regex("Ignoring connection 2 on the database port"));
            Connect(Stranger);

            Assert.That(Authenticate(Stranger, "another-deployments-secret"), Is.EqualTo(AckResponseCode.Error));
            Assert.That(Authenticate(Stranger, string.Empty), Is.EqualTo(AckResponseCode.Error), "a server which has no secret set");
            Assert.That(AskForAUsername(Stranger), Is.EqualTo(new[] { AckResponseCode.Error, AckResponseCode.Error }), "still not read");
        }

        [Test]
        public void AProofReplayedLaterIsRefused()
        {
            // What the server logs about each refusal, in order. An error nobody expected fails the test
            LogAssert.Expect(LogType.Error, new Regex("Refused connection 2: its clock differs"));
            LogAssert.Expect(LogType.Error, new Regex("Ignoring connection 2 on the database port"));
            Connect(Stranger);
            long captured = ServerSecret.Now - ServerSecret.MaxClockDifferenceSeconds - 5;

            Assert.That(Authenticate(Stranger, Secret, captured), Is.EqualTo(AckResponseCode.Error));
            Assert.That(AskForAUsername(Stranger), Is.EqualTo(new[] { AckResponseCode.Error }));
        }

        [Test]
        public void AServerIsNoLongerServedAfterItDisconnects()
        {
            // What the server logs about each refusal, in order. An error nobody expected fails the test
            LogAssert.Expect(LogType.Error, new Regex("Ignoring connection 1 on the database port"));
            Connect(Server);
            Authenticate(Server, Secret);

            _handler.OnServerReceive(TestTransport.Disconnect(Server));
            // The transport gives the same connection ID to whoever connects next.
            Connect(Server);

            Assert.That(AskForAUsername(Server), Is.EqualTo(new[] { AckResponseCode.Success }), "only the earlier proof's answer");
        }

        [Test]
        public void ARequestAnAddonAddedIsBehindTheGateToo()
        {
            // What the server logs about each refusal, in order. An error nobody expected fails the test
            LogAssert.Expect(LogType.Error, new Regex("Ignoring connection 2 on the database port"));
            // Addons register their own database requests like this, from the RegisterMessages dev extension.
            _database.Manager.RegisterRequestToServer<EmptyMessage, EmptyMessage>(AddonRequest, (requestHandler, request, result) =>
            {
                _addonRequestsHandled++;
                result.InvokeSuccess(EmptyMessage.Value);
                return default;
            });
            Connect(Stranger);
            Connect(Server);
            Authenticate(Server, Secret);

            _handler.OnServerReceive(TestTransport.Request(Stranger, MMOMessageTypes.Request, AddonRequest, ++_requestId, EmptyMessage.Value));
            Assert.That(_addonRequestsHandled, Is.Zero);

            _handler.OnServerReceive(TestTransport.Request(Server, MMOMessageTypes.Request, AddonRequest, ++_requestId, EmptyMessage.Value));
            Assert.That(_addonRequestsHandled, Is.EqualTo(1));
        }

        [Test]
        public void WithoutASecretAServerOnThisMachineIsServedAtOnce()
        {
            ServerSecret.Clear();
            Assert.That(_handler.Gate.OnPeerConnected(Server, IPAddress.Loopback, 0), Is.True);

            Assert.That(AskForAUsername(Server), Is.EqualTo(new[] { AckResponseCode.Success }));
            // A current server still sends its (empty) proof, and is told that it is in.
            Assert.That(Authenticate(Server, string.Empty), Is.EqualTo(AckResponseCode.Success));
        }

        [Test]
        public void WithoutASecretAnotherMachineIsDisconnectedAndNotRead()
        {
            // What the server logs about each refusal, in order. An error nobody expected fails the test
            LogAssert.Expect(LogType.Error, new Regex("Refused connection 2 from an unknown address to the database port"));
            LogAssert.Expect(LogType.Error, new Regex("Refused connection 3 from 203\\.0\\.113\\.9 to the database port"));
            ServerSecret.Clear();

            // The test transport cannot tell where a peer is, which is refused like a far address.
            Connect(Stranger);
            Assert.That(_transport.Disconnected, Is.EqualTo(new[] { Stranger }));

            // Its packets can already be waiting behind the connect event.
            Assert.That(AskForAUsername(Stranger), Is.Empty);
            _handler.OnServerReceive(TestTransport.Request(Stranger, MMOMessageTypes.Request, DatabaseRequestTypes.ServerAuthenticate, ++_requestId,
                DbServerAuthenticateMessage.Create(string.Empty, ServerSecret.Now)));
            Assert.That(_transport.ServerSent, Is.Empty);

            Assert.That(_handler.Gate.OnPeerConnected(3, IPAddress.Parse("203.0.113.9"), 0), Is.False);
            Assert.That(_handler.Gate.IsAuthorized(3), Is.False);
        }
    }
}
#endif
