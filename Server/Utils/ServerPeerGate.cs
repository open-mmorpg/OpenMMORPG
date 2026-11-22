#if NET || NETCOREAPP || ((UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE)
using LiteNetLib.Utils;
using LiteNetLibManager;
using System;
using System.Collections.Generic;
using System.Net;

namespace MultiplayerARPG.MMO
{
    /// <summary>
    /// Decides which connections a server-to-server port (cluster, database) serves.
    /// With a <see cref="ServerSecret"/>: a connection is served after it proved that it has the secret,
    /// until then only the one request which carries the proof is read, and it is dropped if the proof does not come.
    /// Without a secret (which servers started by MMOServerInstance or the .NET host never are: outside the editor they refuse to start,
    /// see <see cref="ServerSecret.Problem"/>, and the editor makes one for the session, see <see cref="ServerSecret.SetForEditor"/>):
    /// only a connection from this machine (loopback) is served, any other is dropped as it connects.
    /// </summary>
    public sealed class ServerPeerGate
    {
        /// <summary>
        /// How long a connection may stay without proving that it has the secret
        /// </summary>
        public const int AuthorizeTimeoutSeconds = 10;

        private readonly string _logTag;
        private readonly string _portName;
        private readonly HashSet<long> _authorized = new HashSet<long>();
        // Connection ID -> when it connected (Unix milliseconds), for connections which did not prove the secret yet
        private readonly Dictionary<long, long> _waiting = new Dictionary<long, long>();
        private readonly HashSet<long> _reported = new HashSet<long>();
        // Connections which were refused as they connected. Their packets can already be waiting behind the
        // connect event, so they stay refused until the disconnect event comes
        private readonly HashSet<long> _refused = new HashSet<long>();
        // Proofs which were accepted, with the time they carry. A proof is accepted once, so a copy of it
        // which someone captured on the network is refused. They only get here with a valid hash, so a peer without the secret cannot grow this
        private readonly Dictionary<string, long> _usedProofs = new Dictionary<string, long>();
        private readonly List<string> _expiredProofs = new List<string>();

        public ServerPeerGate(string logTag, string portName)
        {
            _logTag = logTag;
            _portName = portName;
        }

        public static long NowMilliseconds { get { return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); } }

        public bool IsAuthorized(long connectionId)
        {
            return _authorized.Contains(connectionId);
        }

        public void Authorize(long connectionId)
        {
            if (_refused.Contains(connectionId))
                return;
            _authorized.Add(connectionId);
            _waiting.Remove(connectionId);
        }

        /// <summary>
        /// Call when a peer connected, returns `FALSE` if the peer must be disconnected
        /// </summary>
        public bool OnPeerConnected(long connectionId, ITransport transport)
        {
            IPAddress address;
            TryGetPeerAddress(transport, connectionId, out address);
            return OnPeerConnected(connectionId, address, NowMilliseconds);
        }

        /// <summary>
        /// Call when a peer connected, returns `FALSE` if the peer must be disconnected
        /// </summary>
        /// <param name="connectionId"></param>
        /// <param name="address">Where it connected from, `NULL` if the transport cannot tell</param>
        /// <param name="nowMilliseconds"></param>
        public bool OnPeerConnected(long connectionId, IPAddress address, long nowMilliseconds)
        {
            _authorized.Remove(connectionId);
            _reported.Remove(connectionId);
            _refused.Remove(connectionId);
            if (ServerSecret.IsSet)
            {
                _waiting[connectionId] = nowMilliseconds;
                return true;
            }
            _waiting.Remove(connectionId);
            if (IsThisMachine(address))
            {
                _authorized.Add(connectionId);
                return true;
            }
            _refused.Add(connectionId);
            Logging.LogError(_logTag, $"Refused connection {connectionId} from {(address == null ? "an unknown address" : address.ToString())} to the {_portName} port: no `{ProcessArguments.CONFIG_SERVER_SECRET}` is set, so only servers on this machine (127.0.0.1) are accepted. Set the same `{ProcessArguments.CONFIG_SERVER_SECRET}` on every server to let other machines in.");
            return false;
        }

        public void OnPeerDisconnected(long connectionId)
        {
            _authorized.Remove(connectionId);
            _waiting.Remove(connectionId);
            _reported.Remove(connectionId);
            _refused.Remove(connectionId);
        }

        /// <summary>
        /// Should a packet from this connection be read? Yes for a connection which is served,
        /// for any other only the request which carries its proof (`openRequestType`).
        /// It does not move the reader.
        /// </summary>
        public bool Accepts(long connectionId, NetDataReader reader, ushort requestMessageType, ushort openRequestType)
        {
            if (_authorized.Contains(connectionId))
                return true;
            if (_refused.Contains(connectionId))
                return false;
            if (IsRequest(reader, requestMessageType, openRequestType))
                return true;
            if (_reported.Add(connectionId))
                Logging.LogError(_logTag, $"Ignoring connection {connectionId} on the {_portName} port: it did not prove that it has this deployment's `{ProcessArguments.CONFIG_SERVER_SECRET}`.");
            return false;
        }

        /// <summary>
        /// Connections which stayed longer than <see cref="AuthorizeTimeoutSeconds"/> without proving the secret, they are forgotten here, disconnect them.
        /// </summary>
        public void TakeTimedOut(long nowMilliseconds, List<long> result)
        {
            if (_waiting.Count == 0)
                return;
            foreach (KeyValuePair<long, long> waiting in _waiting)
            {
                if (nowMilliseconds - waiting.Value > AuthorizeTimeoutSeconds * 1000L)
                    result.Add(waiting.Key);
            }
            for (int i = 0; i < result.Count; ++i)
            {
                _waiting.Remove(result[i]);
            }
        }

        /// <summary>
        /// Call with a proof whose hash is valid and say `FALSE` if this one was accepted before (a replay).
        /// A proof is not kept longer than <see cref="ServerSecret.MaxClockDifferenceSeconds"/> after its time,
        /// as <see cref="ServerSecret.Check"/> refuses it by then.
        /// It does not stop someone who blocks the original packet and sends the copy instead: nothing stops
        /// that without encrypting the traffic.
        /// </summary>
        public bool TryUseProof(string hash, long proofTime, long now)
        {
            _expiredProofs.Clear();
            foreach (KeyValuePair<string, long> used in _usedProofs)
            {
                if (now - used.Value > ServerSecret.MaxClockDifferenceSeconds)
                    _expiredProofs.Add(used.Key);
            }
            for (int i = 0; i < _expiredProofs.Count; ++i)
            {
                _usedProofs.Remove(_expiredProofs[i]);
            }
            if (_usedProofs.ContainsKey(hash))
                return false;
            _usedProofs[hash] = proofTime;
            return true;
        }

        public void Clear()
        {
            _usedProofs.Clear();
            _authorized.Clear();
            _waiting.Clear();
            _reported.Clear();
            _refused.Clear();
        }

        /// <summary>
        /// Loopback only. A packet from outside cannot carry a loopback source address (every system drops it),
        /// while one carrying this machine's own public address can be forged, so that is not counted as this machine.
        /// </summary>
        public static bool IsThisMachine(IPAddress address)
        {
            if (address == null)
                return false;
            if (address.IsIPv4MappedToIPv6)
                address = address.MapToIPv4();
            return IPAddress.IsLoopback(address);
        }

        public static bool TryGetPeerAddress(ITransport transport, long connectionId, out IPAddress address)
        {
            address = null;
            LiteNetLibTransport liteNetLibTransport = transport as LiteNetLibTransport;
            if (liteNetLibTransport == null || liteNetLibTransport.Server == null)
                return false;
            // A LiteNetLib peer is its remote end point
            IPEndPoint endPoint = liteNetLibTransport.Server.GetPeerById((int)connectionId);
            if (endPoint == null)
                return false;
            address = endPoint.Address;
            return address != null;
        }

        /// <summary>
        /// The kit's transport lets a connected peer move to another address (for players who change networks).
        /// Servers do not roam, and the address is what this gate checked, so turn that off for a server-to-server port.
        /// </summary>
        public static void PinPeerAddresses(ITransport transport)
        {
            LiteNetLibTransport liteNetLibTransport = transport as LiteNetLibTransport;
            if (liteNetLibTransport != null && liteNetLibTransport.Server != null)
                liteNetLibTransport.Server.AllowPeerAddressChange = false;
        }

        /// <summary>
        /// Is this packet a request of `requestType`? It does not move the reader.
        /// </summary>
        public static bool IsRequest(NetDataReader reader, ushort requestMessageType, ushort requestType)
        {
            if (reader == null)
                return false;
            int position = reader.Position;
            try
            {
                return reader.GetPackedUShort() == requestMessageType && reader.GetPackedUShort() == requestType;
            }
            catch (Exception)
            {
                // Malformed packet
                return false;
            }
            finally
            {
                reader.SetPosition(position);
            }
        }
    }
}
#endif
