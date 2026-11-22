#if NET || NETCOREAPP || ((UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE)
using LiteNetLibManager;
using System.Collections.Generic;

namespace MultiplayerARPG.MMO
{
    /// <summary>
    /// The database server's side of its connections. Every packet passes its <see cref="ServerPeerGate"/> before
    /// any handler sees it, so requests are served only for a server of this deployment, whoever registered the handler.
    /// </summary>
    public class DatabaseServerHandler : LiteNetLibServer
    {
        public ServerPeerGate Gate { get; private set; }
        private readonly List<long> _timedOutConnectionIds = new List<long>();

        public DatabaseServerHandler(LiteNetLibManager.LiteNetLibManager manager) : base(manager)
        {
            Gate = new ServerPeerGate(nameof(DatabaseNetworkManager), "database");
        }

        protected override void OnStartServer()
        {
            base.OnStartServer();
            ServerPeerGate.PinPeerAddresses(Transport);
        }

        protected override void OnStopServer()
        {
            base.OnStopServer();
            Gate.Clear();
        }

        public override void OnServerReceive(TransportEventData eventData)
        {
            switch (eventData.type)
            {
                case ENetworkEvent.ConnectEvent:
                    base.OnServerReceive(eventData);
                    if (!Gate.OnPeerConnected(eventData.connectionId, Transport))
                        Transport.ServerDisconnect(eventData.connectionId);
                    return;
                case ENetworkEvent.DataEvent:
                    if (!Gate.Accepts(eventData.connectionId, eventData.reader, RequestMessageType, DatabaseRequestTypes.ServerAuthenticate))
                        return;
                    break;
                case ENetworkEvent.DisconnectEvent:
                    Gate.OnPeerDisconnected(eventData.connectionId);
                    break;
            }
            base.OnServerReceive(eventData);
        }

        /// <summary>
        /// Disconnect connections which did not prove the secret in time
        /// </summary>
        public void DisconnectUnauthenticatedPeers()
        {
            if (!IsNetworkActive)
                return;
            _timedOutConnectionIds.Clear();
            Gate.TakeTimedOut(ServerPeerGate.NowMilliseconds, _timedOutConnectionIds);
            for (int i = 0; i < _timedOutConnectionIds.Count; ++i)
            {
                Logging.Log(nameof(DatabaseNetworkManager), $"Disconnecting [{_timedOutConnectionIds[i]}]: it did not prove the server secret within {ServerPeerGate.AuthorizeTimeoutSeconds} seconds");
                Transport.ServerDisconnect(_timedOutConnectionIds[i]);
            }
        }
    }
}
#endif
