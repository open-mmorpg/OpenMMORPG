using System.Collections.Concurrent;

namespace MultiplayerARPG.MMO
{
    /// <summary>
    /// Runs what the servers await on the host's loop thread, as Unity does on its main thread.
    /// Without it that code resumes on thread-pool threads, several at once, and the kit's server
    /// code (shared packet writers, plain dictionaries) is written for one thread.
    /// </summary>
    public sealed class LoopSynchronizationContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback callback, object state)> _posted = new ConcurrentQueue<(SendOrPostCallback, object)>();

        public override void Post(SendOrPostCallback callback, object state)
        {
            _posted.Enqueue((callback, state));
        }

        /// <summary>Runs what was queued before this call. The loop calls it once a frame.</summary>
        public void Run()
        {
            for (int waiting = _posted.Count; waiting > 0 && _posted.TryDequeue(out (SendOrPostCallback callback, object state) posted); --waiting)
                posted.callback(posted.state);
        }
    }
}
