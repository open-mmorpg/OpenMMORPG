#if NET || NETCOREAPP || ((UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE)
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;

namespace MultiplayerARPG.MMO
{
    /// <summary>
    /// Runs slow work (password hashing) on worker threads of its own and hands each result back
    /// in <see cref="Update"/>, which the loop that asked for it calls every frame.
    /// </summary>
    public class OffLoopWorkQueue
    {
        private readonly object _gate = new object();
        private readonly Queue<Action> _waiting = new Queue<Action>();
        private readonly ConcurrentQueue<Action> _finished = new ConcurrentQueue<Action>();
        private readonly int _maxWorkers;
        private int _workers;

        /// <summary>Results that wait for the next <see cref="Update"/>.</summary>
        public int ReadyCount { get { return _finished.Count; } }

        // Half the cores: the rest stay free for the server loops and the map servers
        public OffLoopWorkQueue() : this(Environment.ProcessorCount / 2)
        {
        }

        public OffLoopWorkQueue(int maxWorkers)
        {
            _maxWorkers = Math.Max(1, maxWorkers);
        }

        /// <summary>The returned task finishes inside a later <see cref="Update"/>.</summary>
        public UniTask<T> Run<T>(Func<T> work)
        {
            UniTaskCompletionSource<T> completion = new UniTaskCompletionSource<T>();
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
            lock (_gate)
            {
                if (_workers >= _maxWorkers)
                {
                    _waiting.Enqueue(job);
                    return completion.Task;
                }
                ++_workers;
            }
            Thread worker = new Thread(Work);
            worker.IsBackground = true;
            worker.Start(job);
            return completion.Task;
        }

        // A worker runs its job, then whatever waits, and ends when nothing does
        private void Work(object state)
        {
            Action job = (Action)state;
            while (job != null)
            {
                job();
                lock (_gate)
                {
                    if (_waiting.Count > 0)
                        job = _waiting.Dequeue();
                    else
                    {
                        --_workers;
                        job = null;
                    }
                }
            }
        }

        public void Update()
        {
            while (_finished.TryDequeue(out Action complete))
                complete();
        }
    }
}
#endif
