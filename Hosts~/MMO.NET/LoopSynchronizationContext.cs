using System.Collections.Concurrent;
using LiteNetLibManager;

namespace MultiplayerARPG.MMO
{
    /// <summary>
    /// Brings every `await` in the servers back to the host's loop, the way Unity brings it back
    /// to the main thread.
    ///
    /// A console program has no such thing by default: code that awaits a Task carries on on
    /// whatever thread-pool thread the Task finished on. The LiteNetLibManager these hosts
    /// compile waits for the answer to a request with `await Task.Delay(100)`, so every server
    /// handler that asked the database or the cluster something carried on beside the loop, and
    /// several of them at once. The kit's server code is written for one thread: its packet
    /// writers are shared and its tables are plain dictionaries. Two logins that overlapped could
    /// write into the same packet, and the database server then got a request it could not read.
    ///
    /// With this installed on the loop's thread, a continuation is queued instead, and
    /// <see cref="Run"/> runs what is queued, once a frame, on that thread.
    /// </summary>
    public sealed class LoopSynchronizationContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback callback, object state)> _posted = new ConcurrentQueue<(SendOrPostCallback, object)>();
        private readonly int _loopThreadId = Environment.CurrentManagedThreadId;

        public override void Post(SendOrPostCallback callback, object state)
        {
            _posted.Enqueue((callback, state));
        }

        public override void Send(SendOrPostCallback callback, object state)
        {
            if (Environment.CurrentManagedThreadId == _loopThreadId)
            {
                callback(state);
                return;
            }
            using (ManualResetEventSlim done = new ManualResetEventSlim(false))
            {
                Exception thrown = null;
                Post(_ =>
                {
                    try
                    {
                        callback(state);
                    }
                    catch (Exception ex)
                    {
                        thrown = ex;
                    }
                    finally
                    {
                        done.Set();
                    }
                }, null);
                done.Wait();
                if (thrown != null)
                    throw new System.Reflection.TargetInvocationException(thrown);
            }
        }

        public override SynchronizationContext CreateCopy()
        {
            return this;
        }

        /// <summary>
        /// Runs what has been queued up to now. Whatever those queue in turn waits for the next
        /// call, so code that keeps yielding cannot hold the loop here.
        /// </summary>
        public void Run()
        {
            for (int waiting = _posted.Count; waiting > 0 && _posted.TryDequeue(out (SendOrPostCallback callback, object state) posted); --waiting)
            {
                try
                {
                    posted.callback(posted.state);
                }
                catch (Exception ex)
                {
                    // An `async void` method that throws ends up here. Off the loop it would end the process.
                    Logging.LogError(nameof(LoopSynchronizationContext), "Error in code that carried on after an await");
                    Logging.LogException(nameof(LoopSynchronizationContext), ex);
                }
            }
        }
    }
}
