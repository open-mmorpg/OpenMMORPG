using LiteNetLib.Utils;
using System.Globalization;

namespace MultiplayerARPG.MMO
{
    public struct RequestAppServerRegisterMessage : INetSerializable
    {
        public CentralServerPeerInfo peerInfo;
        public long time { get; private set; }
        public string hash { get; private set; }
        /// <summary>
        /// Random, new for every registration and part of the hash, so that two registrations never have the same hash
        /// and the server can refuse one which it has seen (see <see cref="ServerPeerGate.TryUseProof"/>)
        /// </summary>
        public string nonce { get; private set; }

        public void Deserialize(NetDataReader reader)
        {
            peerInfo.Deserialize(reader);
            time = reader.GetPackedLong();
            hash = reader.GetString();
            nonce = reader.GetString();
        }

        public void Serialize(NetDataWriter writer)
        {
            time = ServerSecret.Now;
            // A new message has none yet. One which was received keeps its own, so that sending it on writes the same registration
            if (string.IsNullOrEmpty(nonce))
                nonce = ServerSecret.NewNonce();
            hash = ComputeHash(ServerSecret.Value, time, nonce);
            peerInfo.Serialize(writer);
            writer.PutPackedLong(time);
            writer.Put(hash);
            writer.Put(nonce ?? string.Empty);
        }

        /// <summary>
        /// With a secret: a hash which only a server that has the secret can make. It covers who registers and where
        /// players will be sent, so a captured registration cannot be changed to point at another machine.
        /// Without a secret: the old hash, which any build of the kit can make, so builds from before the secret existed still register.
        /// </summary>
        public string ComputeHash(string secret, long atTime, string atNonce)
        {
            if (string.IsNullOrEmpty(secret))
                return ClusterServer.GetAppServerRegisterHash(peerInfo.peerType, atTime);
            return ServerSecret.ComputeHash(secret, ServerSecret.PurposeClusterRegister, atTime,
                ((byte)peerInfo.peerType).ToString(CultureInfo.InvariantCulture),
                peerInfo.networkAddress,
                peerInfo.networkPort.ToString(CultureInfo.InvariantCulture),
                peerInfo.channelId,
                peerInfo.refId,
                atNonce);
        }

        public bool ValidateHash()
        {
            return CheckHash(ServerSecret.Value, ServerSecret.Now) == ServerSecretCheck.Valid;
        }

        public ServerSecretCheck CheckHash(string secret, long now)
        {
            if (string.IsNullOrEmpty(hash))
                return ServerSecretCheck.NoHash;
            if (string.IsNullOrEmpty(secret))
            {
                // No secret to prove, this only tells that the peer is a build of the kit, as it always did
                return hash.Equals(ComputeHash(secret, time, nonce)) ? ServerSecretCheck.Valid : ServerSecretCheck.WrongHash;
            }
            return ServerSecret.Check(secret, hash, ServerSecret.PurposeClusterRegister, time, now,
                ((byte)peerInfo.peerType).ToString(CultureInfo.InvariantCulture),
                peerInfo.networkAddress,
                peerInfo.networkPort.ToString(CultureInfo.InvariantCulture),
                peerInfo.channelId,
                peerInfo.refId,
                nonce);
        }
    }
}
