#if NET || NETCOREAPP || ((UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE)
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;

namespace MultiplayerARPG.MMO
{
    /// <summary>
    /// Runs slow CPU work on worker threads and hands each result back on the loop that asked for
    /// it, so that loop keeps running meanwhile. The loop's owner calls <see cref="Update"/>
    /// every frame; whatever awaits <see cref="Run{T}"/> continues inside that call, on the
    /// loop's own thread.
    ///
    /// It exists for password hashing. A hash is slow on purpose (tens of milliseconds under
    /// .NET, seconds under Unity's Mono), and a server loop that hashes inline does nothing else
    /// for that long: it accepts no connections and answers no other request, for every server
    /// that shares the loop.
    ///
    /// The workers are threads of its own, not thread-pool threads, so a burst of logins cannot
    /// starve the pool the rest of the server runs on. At most <see cref="MaxWorkers"/> run at
    /// once; work beyond that waits its turn.
    /// </summary>
    public class OffLoopWorkQueue
    {
        private readonly object _gate = new object();
        private readonly Queue<Action> _waiting = new Queue<Action>();
        private readonly ConcurrentQueue<Action> _finished = new ConcurrentQueue<Action>();
        private int _workers;
        private int _maxWorkers;
        private int _unfinished;

        /// <summary>Half the machine's cores, at least one: the rest stay free for the server loops and the map servers.</summary>
        public static int DefaultMaxWorkers
        {
            get { return Math.Max(1, Environment.ProcessorCount / 2); }
        }

        /// <summary>How many pieces of work may run at the same time. At least one.</summary>
        public int MaxWorkers
        {
            get { return _maxWorkers; }
            set { _maxWorkers = Math.Max(1, value); }
        }

        /// <summary>Work that was asked for and has not been handed back by <see cref="Update"/> yet.</summary>
        public int UnfinishedCount
        {
            get { return Volatile.Read(ref _unfinished); }
        }

        /// <summary>Results that are ready and wait for the next <see cref="Update"/>.</summary>
        public int ReadyCount
        {
            get { return _finished.Count; }
        }

        public OffLoopWorkQueue() : this(DefaultMaxWorkers)
        {
        }

        public OffLoopWorkQueue(int maxWorkers)
        {
            MaxWorkers = maxWorkers;
        }

        /// <summary>
        /// Runs <paramref name="work"/> on a worker thread. The returned task finishes during a
        /// later <see cref="Update"/>, with the result or with the exception the work threw.
        /// The work must touch nothing that belongs to the loop: it runs beside it.
        /// </summary>
        public UniTask<T> Run<T>(Func<T> work)
        {
            UniTaskCompletionSource<T> completion = new UniTaskCompletionSource<T>();
            Interlocked.Increment(ref _unfinished);
            Action job = () =>
            {
                try
                {
                    T result = work();
                    _finished.Enqueue(() => completion.TrySetResult(result));
                }
                catch (Exception ex)
                {
                    _finished.Enqueue(() => completion.TrySetException(ex));
                }
            };
            bool startWorker = false;
            lock (_gate)
            {
                if (_workers < _maxWorkers)
                {
                    ++_workers;
                    startWorker = true;
                }
                else
                {
                    _waiting.Enqueue(job);
                }
            }
            if (startWorker)
            {
                Thread worker = new Thread(Work);
                worker.IsBackground = true;
                worker.Name = nameof(OffLoopWorkQueue);
                worker.Start(job);
            }
            return completion.Task;
        }

        /// <summary>A worker runs the job it was started for, then whatever waits, and ends when nothing does.</summary>
        private void Work(object state)
        {
            Action job = (Action)state;
            while (job != null)
            {
                job();
                lock (_gate)
                {
                    if (_waiting.Count > 0)
                    {
                        job = _waiting.Dequeue();
                    }
                    else
                    {
                        --_workers;
                        job = null;
                    }
                }
            }
        }

        /// <summary>
        /// Hands back the results that are ready. Call it from the loop, every frame: the code
        /// that awaited <see cref="Run{T}"/> carries on here.
        /// </summary>
        public void Update()
        {
            while (_finished.TryDequeue(out Action complete))
            {
                Interlocked.Decrement(ref _unfinished);
                complete();
            }
        }
    }
}
#endif
