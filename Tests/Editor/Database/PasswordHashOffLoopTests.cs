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
    /// Password hashing is slow on purpose, so a database server runs it beside its loop and
    /// answers on the loop afterwards. Hashed inline, every login stopped the loop for the
    /// length of a hash: the server accepted no connections meanwhile, and clients that arrived
    /// during a burst of logins gave up. These tests hold the server to that: nothing is
    /// answered before the loop's Update, the loop's thread is the one that answers and writes,
    /// and the result is the same as hashing inline.
    /// </summary>
    [Category(TestCategories.Harnessed)]
    public class PasswordHashOffLoopTests : KitTestFixture
    {
        private const string Md5OfSecret = "5ebe2294ecd0e0f08eab7690d2a6ee69";

        private TestDatabaseServer _server;
        private OffLoopWorkQueue _work;
        private string _userId;

        [SetUp]
        public void SetUp()
        {
            _server = TestDatabaseServer.Create(Objects);
            // Created inline, before the queue is attached
            _userId = _server.Db.CreateUser("tester", "secret");
            _work = _server.Manager.BackgroundWork;
            _server.Db.Database.BackgroundWork = _work;
        }

        /// <summary>Waits for the workers, without running the loop.</summary>
        private void WaitUntilReady(int results = 1)
        {
            Stopwatch waited = Stopwatch.StartNew();
            while (_work.ReadyCount < results)
            {
                Assert.That(waited.Elapsed.TotalSeconds, Is.LessThan(60), "the workers did not finish");
                Thread.Sleep(1);
            }
        }

        /// <summary>One frame of the database server, as Unity or the .NET host would run it.</summary>
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
        public void ALoginIsAnsweredByTheLoopOnceItsHashIsDone()
        {
            List<TestDatabaseServer.Reply<ValidateUserLoginResp>> replies =
                Ask<ValidateUserLoginReq, ValidateUserLoginResp>("ValidateUserLogin", new ValidateUserLoginReq() { Username = "tester", Password = "secret" });

            Assert.That(replies, Is.Empty, "the handler returned without hashing: the loop is free");
            WaitUntilReady();
            Assert.That(replies, Is.Empty, "a worker thread must not answer, only the loop");

            RunTheLoop();

            Assert.That(replies, Has.Count.EqualTo(1));
            Assert.That(replies[0].Code, Is.EqualTo(AckResponseCode.Success));
            Assert.That(replies[0].Response.UserId, Is.EqualTo(_userId));
            Assert.That(_work.UnfinishedCount, Is.Zero);
        }

        [Test]
        public void AWrongPasswordStillReturnsNoUserId()
        {
            List<TestDatabaseServer.Reply<ValidateUserLoginResp>> replies =
                Ask<ValidateUserLoginReq, ValidateUserLoginResp>("ValidateUserLogin", new ValidateUserLoginReq() { Username = "tester", Password = "Secret" });
            WaitUntilReady();
            RunTheLoop();

            Assert.That(replies, Has.Count.EqualTo(1));
            Assert.That(replies[0].Response.UserId, Is.Empty);
        }

        [Test]
        public void AnUnknownUserIsAnsweredAtOnceBecauseThereIsNothingToHash()
        {
            List<TestDatabaseServer.Reply<ValidateUserLoginResp>> replies =
                Ask<ValidateUserLoginReq, ValidateUserLoginResp>("ValidateUserLogin", new ValidateUserLoginReq() { Username = "nobody", Password = "secret" });

            Assert.That(replies, Has.Count.EqualTo(1));
            Assert.That(replies[0].Response.UserId, Is.Empty);
            Assert.That(_work.UnfinishedCount, Is.Zero);
        }

        [Test]
        public void ManyLoginsAtOnceAreAllAnsweredRightAndOnlyByTheLoop()
        {
            int loopThread = Thread.CurrentThread.ManagedThreadId;
            List<int> answeringThreads = new List<int>();
            List<string> userIds = new List<string>();
            const int logins = 12;
            for (int i = 0; i < logins; ++i)
            {
                // Every third one has the wrong password
                string password = i % 3 == 2 ? "wrong" : "secret";
                AnswerOn(_server.Db.Database.ValidateUserLogin("tester", password), answeringThreads, userIds).Forget();
            }
            Assert.That(userIds, Is.Empty);

            WaitUntilReady(logins);
            RunTheLoop();

            Assert.That(userIds, Has.Count.EqualTo(logins));
            Assert.That(userIds.FindAll(id => id == _userId), Has.Count.EqualTo(8));
            Assert.That(userIds.FindAll(id => id == string.Empty), Has.Count.EqualTo(4));
            Assert.That(answeringThreads, Is.All.EqualTo(loopThread), "SQLite and the reply belong to the loop's thread");
        }

        private static async UniTaskVoid AnswerOn(UniTask<string> login, List<int> threads, List<string> userIds)
        {
            string userId = await login;
            threads.Add(Thread.CurrentThread.ManagedThreadId);
            userIds.Add(userId);
        }

        [Test]
        public void AnOutdatedHashIsReplacedAfterARightPassword()
        {
            _server.Db.Database.ExecuteNonQuery("UPDATE userlogin SET password=@password WHERE id=@id",
                new SqliteParameter("@password", Md5OfSecret), new SqliteParameter("@id", _userId));

            List<TestDatabaseServer.Reply<ValidateUserLoginResp>> replies =
                Ask<ValidateUserLoginReq, ValidateUserLoginResp>("ValidateUserLogin", new ValidateUserLoginReq() { Username = "tester", Password = "secret" });
            WaitUntilReady();
            Assert.That(StoredPassword(), Is.EqualTo(Md5OfSecret), "written by the loop, not by the worker");
            RunTheLoop();

            Assert.That(replies[0].Response.UserId, Is.EqualTo(_userId));
            Assert.That(StoredPassword(), Does.StartWith("p3$1000$"));
            // And the new hash is the same password
            _server.Db.Database.BackgroundWork = null;
            Assert.That(TestAsync.Wait(_server.Db.Database.ValidateUserLogin("tester", "secret")), Is.EqualTo(_userId));
        }

        [Test]
        public void AWrongPasswordLeavesAnOutdatedHashAlone()
        {
            _server.Db.Database.ExecuteNonQuery("UPDATE userlogin SET password=@password WHERE id=@id",
                new SqliteParameter("@password", Md5OfSecret), new SqliteParameter("@id", _userId));

            Ask<ValidateUserLoginReq, ValidateUserLoginResp>("ValidateUserLogin", new ValidateUserLoginReq() { Username = "tester", Password = "other" });
            WaitUntilReady();
            RunTheLoop();

            Assert.That(StoredPassword(), Is.EqualTo(Md5OfSecret));
        }

        private string StoredPassword()
        {
            return (string)_server.Db.Database.ExecuteScalar("SELECT password FROM userlogin WHERE id=@id", new SqliteParameter("@id", _userId));
        }

        [Test]
        public void ANewAccountIsWrittenByTheLoopOnceItsHashIsDone()
        {
            List<TestDatabaseServer.Reply<EmptyMessage>> replies =
                Ask<CreateUserLoginReq, EmptyMessage>("CreateUserLogin", new CreateUserLoginReq() { Username = "newcomer", Password = "hunter22", Email = string.Empty });

            Assert.That(replies, Is.Empty);
            WaitUntilReady();
            Assert.That(TestAsync.Wait(_server.Db.Database.FindUsername("newcomer")), Is.Zero, "not written by the worker");

            RunTheLoop();

            Assert.That(replies, Has.Count.EqualTo(1));
            Assert.That(replies[0].Code, Is.EqualTo(AckResponseCode.Success));
            _server.Db.Database.BackgroundWork = null;
            Assert.That(TestAsync.Wait(_server.Db.Database.ValidateUserLogin("newcomer", "hunter22")), Is.Not.Empty);
        }

        [Test]
        public void ASecondRequestForANameThatIsStillBeingHashedIsRefused()
        {
            // The loop runs on while the first one hashes, so the second can arrive before the
            // first has written its row. Without the refusal both would be told they succeeded,
            // and only one row can exist.
            List<TestDatabaseServer.Reply<EmptyMessage>> first =
                Ask<CreateUserLoginReq, EmptyMessage>("CreateUserLogin", new CreateUserLoginReq() { Username = "Twin", Password = "hunter22", Email = string.Empty });
            List<TestDatabaseServer.Reply<EmptyMessage>> second =
                Ask<CreateUserLoginReq, EmptyMessage>("CreateUserLogin", new CreateUserLoginReq() { Username = "twin", Password = "hunter23", Email = string.Empty });

            Assert.That(second, Has.Count.EqualTo(1));
            Assert.That(second[0].Code, Is.EqualTo(AckResponseCode.Error));

            WaitUntilReady();
            RunTheLoop();
            Assert.That(first, Has.Count.EqualTo(1));
            Assert.That(first[0].Code, Is.EqualTo(AckResponseCode.Success));

            // The name is free again for a later request to be checked the usual way
            List<TestDatabaseServer.Reply<EmptyMessage>> third =
                Ask<CreateUserLoginReq, EmptyMessage>("CreateUserLogin", new CreateUserLoginReq() { Username = "other", Password = "hunter24", Email = string.Empty });
            Assert.That(third, Is.Empty);
            WaitUntilReady();
            RunTheLoop();
            Assert.That(third[0].Code, Is.EqualTo(AckResponseCode.Success));
        }

        [Test]
        public void TheServerGivesItsDatabaseTheQueueAndTheRoundsOfTheHash()
        {
            _server.Db.Database.BackgroundWork = null;
            _server.Manager.passwordHashIterations = 7;
            // What OnStartServer does before the first request
            TestReflection.Invoke(_server.Manager, "PrepareDatabase");

            Assert.That(_server.Db.Database.BackgroundWork, Is.SameAs(_server.Manager.BackgroundWork));
            Ask<CreateUserLoginReq, EmptyMessage>("CreateUserLogin", new CreateUserLoginReq() { Username = "seven", Password = "hunter22", Email = string.Empty });
            WaitUntilReady();
            RunTheLoop();
            object stored = _server.Db.Database.ExecuteScalar("SELECT password FROM userlogin WHERE username=@username", new SqliteParameter("@username", "seven"));
            Assert.That(stored, Does.StartWith("p3$7$"));
        }

        [Test]
        public void NoMoreThanMaxWorkersRunAtOnceAndTheRestWaitTheirTurn()
        {
            OffLoopWorkQueue work = new OffLoopWorkQueue(2);
            int running = 0, most = 0, done = 0;
            object gate = new object();
            const int jobs = 9;
            for (int i = 0; i < jobs; ++i)
            {
                int number = i;
                Count(work.Run(() =>
                {
                    lock (gate)
                    {
                        ++running;
                        most = Math.Max(most, running);
                    }
                    Thread.Sleep(15);
                    lock (gate)
                        --running;
                    return number;
                })).Forget();
            }

            async UniTaskVoid Count(UniTask<int> job)
            {
                await job;
                ++done;
            }

            Stopwatch waited = Stopwatch.StartNew();
            while (work.ReadyCount < jobs)
            {
                Assert.That(waited.Elapsed.TotalSeconds, Is.LessThan(60), "the workers did not finish");
                Thread.Sleep(1);
            }
            Assert.That(done, Is.Zero, "nothing carries on before Update");
            work.Update();

            Assert.That(done, Is.EqualTo(jobs));
            Assert.That(most, Is.EqualTo(2));
            Assert.That(work.UnfinishedCount, Is.Zero);
        }

        [Test]
        public void WorkThatThrowsFailsItsOwnTaskOnTheLoopAndNoOther()
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

            Stopwatch waited = Stopwatch.StartNew();
            while (work.ReadyCount < 2)
            {
                Assert.That(waited.Elapsed.TotalSeconds, Is.LessThan(60), "the workers did not finish");
                Thread.Sleep(1);
            }
            work.Update();

            Assert.That(caught, Is.InstanceOf<InvalidOperationException>());
            Assert.That(good, Is.EqualTo(7));
        }

        [Test]
        public void WithoutAQueueTheHashRunsInlineAsTheWebApiAndTheOtherTestsNeed()
        {
            _server.Db.Database.BackgroundWork = null;

            Assert.That(TestAsync.Wait(_server.Db.Database.ValidateUserLogin("tester", "secret")), Is.EqualTo(_userId));
            Assert.That(TestAsync.Wait(_server.Db.Database.ValidateUserLogin("tester", "wrong")), Is.Empty);
        }
    }
}
#endif
