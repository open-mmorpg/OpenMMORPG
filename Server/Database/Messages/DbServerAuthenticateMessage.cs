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

        public static DbServerAuthenticateMessage Create(string secret, long time)
        {
            return new DbServerAuthenticateMessage()
            {
                time = time,
                hash = string.IsNullOrEmpty(secret) ? string.Empty : ServerSecret.ComputeHash(secret, ServerSecret.PurposeDatabaseClient, time),
            };
        }

        public ServerSecretCheck Check(string secret, long now)
        {
            return ServerSecret.Check(secret, hash, ServerSecret.PurposeDatabaseClient, time, now);
        }

        public void Deserialize(NetDataReader reader)
        {
            time = reader.GetPackedLong();
            hash = reader.GetString();
        }

        public void Serialize(NetDataWriter writer)
        {
            writer.PutPackedLong(time);
            writer.Put(hash);
        }
    }
}
