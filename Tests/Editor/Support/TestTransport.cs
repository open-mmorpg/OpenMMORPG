using System.Collections.Generic;
using LiteNetLib;
using LiteNetLib.Utils;
using LiteNetLibManager;

namespace OpenMMORPG.Tests
{
    /// <summary>
    /// A transport that opens no socket. Give it to a never-started server (set its Transport),
    /// feed the server packets through OnServerReceive with <see cref="Request{TRequest}"/> and
    /// <see cref="Message"/>, and read back what the server sent and whom it disconnected.
    /// </summary>
    public sealed class TestTransport : ITransport
    {
        public struct Sent
        {
            public long ConnectionId;
            public byte[] Data;

            public NetDataReader Reader()
            {
                return new NetDataReader(Data);
            }
        }

        public readonly List<Sent> ServerSent = new List<Sent>();
        public readonly List<long> Disconnected = new List<long>();

        public int ServerPeersCount { get { return 0; } }
        public int ServerMaxConnections { get { return int.MaxValue; } }
        public bool IsClientStarted { get { return false; } }
        public bool IsServerStarted { get { return true; } }
        public bool HasImplementedPing { get { return false; } }
        public bool IsReliableOnly { get { return true; } }

        public bool StartClient(string address, int port) { return false; }
        public bool ClientSend(byte dataChannel, DeliveryMethod deliveryMethod, NetDataWriter writer) { return false; }
        public bool ClientReceive(out TransportEventData eventData) { eventData = default; return false; }
        public void StopClient() { }
        public bool StartServer(int port, int maxConnections) { return true; }
        public bool ServerReceive(out TransportEventData eventData) { eventData = default; return false; }
        public void StopServer() { }
        public void Destroy() { }
        public long GetClientRtt() { return 0; }
        public long GetServerRtt(long connectionId) { return 0; }

        public bool ServerSend(long connectionId, byte dataChannel, DeliveryMethod deliveryMethod, NetDataWriter writer)
        {
            ServerSent.Add(new Sent() { ConnectionId = connectionId, Data = writer.CopyData() });
            return true;
        }

        public bool ServerDisconnect(long connectionId)
        {
            Disconnected.Add(connectionId);
            return true;
        }

        /// <summary>A peer connecting, as the transport reports it.</summary>
        public static TransportEventData Connect(long connectionId)
        {
            return new TransportEventData() { type = ENetworkEvent.ConnectEvent, connectionId = connectionId };
        }

        public static TransportEventData Disconnect(long connectionId)
        {
            return new TransportEventData() { type = ENetworkEvent.DisconnectEvent, connectionId = connectionId };
        }

        /// <summary>A request packet from a peer, laid out as TransportHandler writes one.</summary>
        public static TransportEventData Request<TRequest>(long connectionId, ushort requestMessageType, ushort requestType, uint requestId, TRequest request)
            where TRequest : INetSerializable
        {
            NetDataWriter writer = new NetDataWriter();
            writer.PutPackedUShort(requestMessageType);
            writer.PutPackedUShort(requestType);
            writer.PutPackedUInt(requestId);
            request.Serialize(writer);
            return Data(connectionId, writer);
        }

        /// <summary>A message packet from a peer.</summary>
        public static TransportEventData Message(long connectionId, ushort messageType, SerializerDelegate serializer)
        {
            NetDataWriter writer = new NetDataWriter();
            TransportHandler.WritePacket(writer, messageType, serializer);
            return Data(connectionId, writer);
        }

        public static TransportEventData Data(long connectionId, NetDataWriter writer)
        {
            return new TransportEventData() { type = ENetworkEvent.DataEvent, connectionId = connectionId, reader = new NetDataReader(writer.CopyData()) };
        }

        /// <summary>The response codes the server sent to a connection, oldest first.</summary>
        public List<AckResponseCode> ResponsesTo(long connectionId, ushort responseMessageType)
        {
            List<AckResponseCode> codes = new List<AckResponseCode>();
            foreach (Sent sent in ServerSent)
            {
                if (sent.ConnectionId != connectionId)
                    continue;
                NetDataReader reader = sent.Reader();
                if (reader.GetPackedUShort() != responseMessageType)
                    continue;
                reader.GetPackedUInt();
                codes.Add(reader.GetValue<AckResponseCode>());
            }
            return codes;
        }

        /// <summary>How many messages of a type the server sent to a connection.</summary>
        public int MessagesTo(long connectionId, ushort messageType)
        {
            int count = 0;
            foreach (Sent sent in ServerSent)
            {
                if (sent.ConnectionId == connectionId && sent.Reader().GetPackedUShort() == messageType)
                    count++;
            }
            return count;
        }
    }
}
