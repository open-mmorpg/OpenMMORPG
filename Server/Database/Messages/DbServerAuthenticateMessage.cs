using LiteNetLib.Utils;

namespace MultiplayerARPG.MMO
{
    /// <summary>
    /// What a server sends to the database server as soon as it connects, to prove that it has the deployment's <see cref="ServerSecret"/>.
    /// The hash is empty when the sender has no secret.
    /// </summary>
    public struct DbServerAuthenticateMessage : INetSerializable
    {
        public long time;
        public string hash;
        /// <summary>
        /// Random, new for every proof and part of the hash, so that two proofs are never the same
        /// and the server can refuse one which it has seen (see <see cref="ServerPeerGate.TryUseProof"/>)
        /// </summary>
        public string nonce;

        public static DbServerAuthenticateMessage Create(string secret, long time)
        {
            string nonce = ServerSecret.NewNonce();
            return new DbServerAuthenticateMessage()
            {
                time = time,
                nonce = nonce,
                hash = string.IsNullOrEmpty(secret) ? string.Empty : ServerSecret.ComputeHash(secret, ServerSecret.PurposeDatabaseClient, time, nonce),
            };
        }

        public ServerSecretCheck Check(string secret, long now)
        {
            return ServerSecret.Check(secret, hash, ServerSecret.PurposeDatabaseClient, time, now, nonce);
        }

        public void Deserialize(NetDataReader reader)
        {
            time = reader.GetPackedLong();
            hash = reader.GetString();
            nonce = reader.GetString();
        }

        public void Serialize(NetDataWriter writer)
        {
            writer.PutPackedLong(time);
            writer.Put(hash);
            writer.Put(nonce ?? string.Empty);
        }
    }
}
