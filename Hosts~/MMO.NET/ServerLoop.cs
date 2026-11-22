using LiteNetLibManager;

namespace MultiplayerARPG.MMO
{
    /// <summary>
    /// Runs one frame of a server so that nothing a peer sends can end the process.
    ///
    /// The LiteNetLibManager these hosts compile (Hosts~/ThirdParty) reads a packet with nothing
    /// around it to catch a failure: a packet cut off before its message type, request type or
    /// request id, or one a message handler cannot read, throws out of the server's update, and
    /// from there out of the program. One such packet from anyone who can reach a port stopped
    /// every server in the process. The kit's own copy, the one Unity compiles, catches this where
    /// the packet is read.
    ///
    /// So the host takes the events from each server's transport itself, one at a time, and hands
    /// them to the server inside a try. A packet that fails is logged and dropped, and the
    /// next one is read as usual. It calls the server's own OnServerReceive, so whatever a server
    /// class does there (the cluster server's bookkeeping, for one) still happens.
    /// </summary>
    public static class ServerLoop
    {
        // A peer can send bad packets as fast as it likes: log a few in full, then count
        private const int FullReportsPerWindow = 5;
        private const long WindowMilliseconds = 10000;
        private static long s_windowStartedAt = Environment.TickCount64;
        private static int s_reportsInWindow;
        private static int s_unloggedInWindow;

        /// <summary>
        /// One frame of a manager, and of another server it runs if it has one (the central
        /// server's cluster server).
        /// </summary>
        public static void Update(LiteNetLibManager.LiteNetLibManager manager, LiteNetLibServer otherServer = null)
        {
            Receive(manager.Server);
            if (otherServer != null)
                Receive(otherServer);
            try
            {
                // Its own receive loop finds nothing left to read, bar a packet that arrived in
                // the moment since. Should that one be unreadable, or anything else in the update
                // throw, the frame ends here and the server carries on with the next one.
                manager.ProcessUpdate();
            }
            catch (Exception ex)
            {
                Report(manager.LogTag, "Error in the server's update, the rest of this frame is skipped", ex);
            }
            ReportUnlogged();
        }

        private static void Receive(LiteNetLibServer server)
        {
            while (server.IsNetworkActive && server.Transport != null && server.Transport.ServerReceive(out TransportEventData eventData))
            {
                try
                {
                    server.OnServerReceive(eventData);
                }
                catch (Exception ex)
                {
                    string what = eventData.type == ENetworkEvent.DataEvent ? "Dropped a packet that could not be read or handled" : $"Error while handling a {eventData.type}";
                    Report(server.LogTag, $"{what}, from connection {eventData.connectionId}", ex);
                }
            }
        }

        private static void Report(string logTag, string message, Exception ex)
        {
            ReportUnlogged();
            if (s_reportsInWindow >= FullReportsPerWindow)
            {
                s_unloggedInWindow++;
                return;
            }
            s_reportsInWindow++;
            Logging.LogError(logTag, message);
            Logging.LogException(logTag, ex);
        }

        /// <summary>Every ten seconds, one line saying how many errors were left out of the log.</summary>
        private static void ReportUnlogged()
        {
            long now = Environment.TickCount64;
            if (now - s_windowStartedAt < WindowMilliseconds)
                return;
            if (s_unloggedInWindow > 0)
                Logging.LogError(nameof(ServerLoop), $"{s_unloggedInWindow} more errors like the ones above in the last {WindowMilliseconds / 1000} seconds were not logged.");
            s_windowStartedAt = now;
            s_reportsInWindow = 0;
            s_unloggedInWindow = 0;
        }
    }
}
