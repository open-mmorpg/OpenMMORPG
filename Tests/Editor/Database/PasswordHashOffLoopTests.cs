// The database server and its SQLite database compile for standalone server builds only.
#if (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Cysharp.Threading.Tasks;
using LiteNetLib.Utils;
using LiteNetLibManager;
using Mono.Data.Sqlite;
using MultiplayerARPG.MMO;
using NUnit.Framework;

namespace OpenMMORPG.Tests
{
    /// <summary>
    /// A database server hashes passwords beside its loop and answers on the loop afterwards, so
    /// a slow hash does not stop it from accepting connections. Nothing may be answered or
    /// written before the loop's Update, and the result must be the same as hashing inline.
    /// </summary>
    [Category(TestCategories.Harnessed)]
    public class PasswordHashOffLoopTests : KitTestFixture
    {
        private TestDatabaseServer _server;
        private OffLoopWorkQueue _work;
        private string _userId;

        [SetUp]
        public void SetUp()
        {
            _server = TestDatabaseServer.Create(Objects);
            _userId = _server.Db.CreateUser("tester", "secret");
            // What OnStartServer does
            TestReflection.Invoke(_server.Manager, "PrepareDatabase");
            _work = _server.Manager.BackgroundWork;
        }

        private static void WaitUntilReady(OffLoopWorkQueue work, int results = 1)
        {
            Stopwatch waited = Stopwatch.StartNew();
            while (work.ReadyCount < results)
            {
                Assert.That(waited.Elapsed.TotalSeconds, Is.LessThan(60), "the workers did not finish");
                Thread.Sleep(1);
            }
        }

        // One frame of the database server
        private void RunTheLoop()
        {
            TestReflection.Invoke(_server.Manager, "Update");
        }

        private List<TestDatabaseServer.Reply<TResponse>> Ask<TRequest, TResponse>(string handler, TRequest data)
            where TRequest : struct, INetSerializable
            where TResponse : INetSerializable
        {
            List<TestDatabaseServer.Reply<TResponse>> replies = new List<TestDatabaseServer.Reply<TResponse>>();
            RequestProceedResultDelegate<TResponse> result = (code, response, extra) => replies.Add(new TestDatabaseServer.Reply<TResponse>() { Code = code, Response = response });
            RequestHandlerData handlerData = new RequestHandlerData(0, 0, null, 1, new NetDataReader(new byte[0]));
            DbRequestMessage<TRequest> request = new DbRequestMessage<TRequest>() { RequestTimeUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), Data = data };
            TestReflection.Invoke(_server.Manager, handler, handlerData, request, result);
            return replies;
        }

        [Test]
        public void TheServerGivesItsDatabaseTheQueue()
        {
            Assert.That(_server.Db.Database.BackgroundWork, Is.SameAs(_server.Manager.BackgroundWork));
        }

        [Test]
        public void ALoginIsAnsweredByTheLoopOnceItsHashIsDone()
        {
            List<TestDatabaseServer.Reply<ValidateUserLoginResp>> replies =
                Ask<ValidateUserLoginReq, ValidateUserLoginResp>("ValidateUserLogin", new ValidateUserLoginReq() { Username = "tester", Password = "secret" });

            Assert.That(replies, Is.Empty, "the handler returned without hashing");
            WaitUntilReady(_work);
            Assert.That(replies, Is.Empty, "a worker thread must not answer");

            RunTheLoop();

            Assert.That(replies, Has.Count.EqualTo(1));
            Assert.That(replies[0].Response.UserId, Is.EqualTo(_userId));
        }

        [Test]
        public void ManyLoginsAtOnceAreAllAnsweredRightAndOnTheLoopThread()
        {
            int loopThread = Thread.CurrentThread.ManagedThreadId;
            List<int> answeringThreads = new List<int>();
            List<string> userIds = new List<string>();
            const int logins = 12;
            for (int i = 0; i < logins; ++i)
                Answer(_server.Db.Database.ValidateUserLogin("tester", i % 3 == 2 ? "wrong" : "secret")).Forget();

            async UniTaskVoid Answer(UniTask<string> login)
            {
                string userId = await login;
                answeringThreads.Add(Thread.CurrentThread.ManagedThreadId);
                userIds.Add(userId);
            }

            WaitUntilReady(_work, logins);
            Assert.That(userIds, Is.Empty);
            RunTheLoop();

            Assert.That(userIds.FindAll(id => id == _userId), Has.Count.EqualTo(8));
            Assert.That(userIds.FindAll(id => id == string.Empty), Has.Count.EqualTo(4));
            Assert.That(answeringThreads, Is.All.EqualTo(loopThread));
        }

        [Test]
        public void AnOutdatedHashIsReplacedByTheLoopAfterARightPassword()
        {
            const string md5OfSecret = "5ebe2294ecd0e0f08eab7690d2a6ee69";
            _server.Db.Database.ExecuteNonQuery("UPDATE userlogin SET password=@password WHERE id=@id",
                new SqliteParameter("@password", md5OfSecret), new SqliteParameter("@id", _userId));

            List<TestDatabaseServer.Reply<ValidateUserLoginResp>> replies =
                Ask<ValidateUserLoginReq, ValidateUserLoginResp>("ValidateUserLogin", new ValidateUserLoginReq() { Username = "tester", Password = "secret" });
            WaitUntilReady(_work);
            Assert.That(StoredPassword(), Is.EqualTo(md5OfSecret), "not written by the worker");
            RunTheLoop();

            Assert.That(replies[0].Response.UserId, Is.EqualTo(_userId));
            Assert.That(StoredPassword(), Does.StartWith("p3$1000$"));
        }

        private string StoredPassword()
        {
            return (string)_server.Db.Database.ExecuteScalar("SELECT password FROM userlogin WHERE id=@id", new SqliteParameter("@id", _userId));
        }

        [Test]
        public void ANewAccountIsWrittenByTheLoopAndASecondRequestForItsNameIsRefusedMeanwhile()
        {
            List<TestDatabaseServer.Reply<EmptyMessage>> first =
                Ask<CreateUserLoginReq, EmptyMessage>("CreateUserLogin", new CreateUserLoginReq() { Username = "Twin", Password = "hunter22", Email = string.Empty });
            List<TestDatabaseServer.Reply<EmptyMessage>> second =
                Ask<CreateUserLoginReq, EmptyMessage>("CreateUserLogin", new CreateUserLoginReq() { Username = "twin", Password = "hunter23", Email = string.Empty });

            Assert.That(first, Is.Empty);
            Assert.That(second[0].Code, Is.EqualTo(AckResponseCode.Error));
            WaitUntilReady(_work);
            Assert.That(TestAsync.Wait(_server.Db.Database.FindUsername("Twin")), Is.Zero, "not written by the worker");

            RunTheLoop();

            Assert.That(first[0].Code, Is.EqualTo(AckResponseCode.Success));
            Assert.That(TestAsync.Wait(_server.Db.Database.FindUsername("Twin")), Is.EqualTo(1));
        }

        [Test]
        public void NoMoreThanMaxWorkersRunAtOnce()
        {
            OffLoopWorkQueue work = new OffLoopWorkQueue(2);
            int running = 0, most = 0, done = 0;
            object gate = new object();
            const int jobs = 9;
            for (int i = 0; i < jobs; ++i)
            {
                Count(work.Run(() =>
                {
                    lock (gate)
                        most = Math.Max(most, ++running);
                    Thread.Sleep(15);
                    lock (gate)
                        --running;
                    return 0;
                })).Forget();
            }

            async UniTaskVoid Count(UniTask<int> job)
            {
                await job;
                ++done;
            }

            WaitUntilReady(work, jobs);
            Assert.That(done, Is.Zero, "nothing carries on before Update");
            work.Update();

            Assert.That(done, Is.EqualTo(jobs));
            Assert.That(most, Is.EqualTo(2));
        }

        [Test]
        public void WorkThatThrowsFailsItsOwnTaskOnly()
        {
            OffLoopWorkQueue work = new OffLoopWorkQueue(1);
            Exception caught = null;
            int good = 0;
            Catch(work.Run<int>(() => throw new InvalidOperationException("bad hash"))).Forget();
            Catch(work.Run(() => 7)).Forget();

            async UniTaskVoid Catch(UniTask<int> job)
            {
                try
                {
                    good += await job;
                }
                catch (Exception ex)
                {
                    caught = ex;
                }
            }

            WaitUntilReady(work, 2);
            work.Update();

            Assert.That(caught, Is.InstanceOf<InvalidOperationException>());
            Assert.That(good, Is.EqualTo(7));
        }
    }
}
#endif
