using LiteNetLibManager;
using MultiplayerARPG;
using MultiplayerARPG.MMO;
using Newtonsoft.Json;

// Usage: OpenMMORPG.SmokeClient [host] [loginPort] [characterCreationData.json]
//        (defaults: 127.0.0.1 7500, and the copy of MMO.NET's Config/characterCreationData.json
//        built beside this program)
// Against a running login server: registers a fresh user, checks a wrong password is refused,
// logs in, validates the token, lists channels, creates a character and lists characters again.
// Exit code 0 only if every step behaves. The character it creates is the first one in the
// creation data, so it follows whatever game data the host was given.
//
// Usage: OpenMMORPG.SmokeClient connections <host> <loginPort> <clients> <expected>
// Opens that many connections to the login server, one after another, and holds them open.
// Exit code 0 only if exactly <expected> of them are let in and, when some were refused, a
// newcomer gets in once one of the connected clients has left. For a login server started with
// -loginMaxConnections <expected>.
//
// Usage: OpenMMORPG.SmokeClient malformed <host> <port> [connectKey]
// Sends a server packets no real client or server would send: empty ones, ones cut off part way
// through what the server reads first, and random bytes. After each one a new connection must
// still be let in, which only happens while the server's loop is running. Exit code 0 only if
// the server takes every packet and stays up. Any server's port will do: the login and database
// servers take the default connect key, the cluster server takes CLUSTER.
//
// Usage: OpenMMORPG.SmokeClient bots <host> <loginPort> <bots> [-ramp seconds] [-register] [-characters] [-hold]
// A load test: that many clients connect and log in at once, or spread over the ramp, and a
// report says how many got through and how long it took them. Exit code 0 only if all did.
// See LoginBots.cs.
if (args.Length > 0 && args[0] == "bots")
    return LoginBots.Run(args);

if (args.Length > 0 && args[0] == "malformed")
{
    if (args.Length < 3)
    {
        Console.WriteLine("FAIL usage: malformed <host> <port> [connectKey]");
        return 1;
    }
    return await MalformedPackets(args[1], int.Parse(args[2]), args.Length > 3 ? args[3] : "SampleConnectKey");
}

if (args.Length > 0 && args[0] == "connections")
{
    if (args.Length < 5)
    {
        Console.WriteLine("FAIL usage: connections <host> <loginPort> <clients> <expected>");
        return 1;
    }
    return await ConnectionLimit(args[1], int.Parse(args[2]), int.Parse(args[3]), int.Parse(args[4]));
}

string host = args.Length > 0 ? args[0] : "127.0.0.1";
int port = args.Length > 1 ? int.Parse(args[1]) : 7500;
string creationDataPath = args.Length > 2 ? args[2] : Path.Combine(AppContext.BaseDirectory, "Config", "characterCreationData.json");

// AvailableCharacters[entityId][dataId] is the character a new player of that kind starts as
CharacterCreationData creationData = JsonConvert.DeserializeObject<CharacterCreationData>(File.ReadAllText(creationDataPath));
if (creationData == null || creationData.AvailableCharacters.Count == 0)
{
    Console.WriteLine($"FAIL no characters in {creationDataPath}");
    return 1;
}
int entityId = creationData.AvailableCharacters.Keys.First();
int dataId = creationData.AvailableCharacters[entityId].Keys.First();
PlayerCharacterData template = creationData.AvailableCharacters[entityId][dataId];
// A game without factions exports none, and its characters have faction 0
int factionId = creationData.AvailableFactionIds.Count > 0 ? creationData.AvailableFactionIds[0] : 0;
string username = "smoke" + DateTime.UtcNow.ToString("HHmmssfff");
const string password = "smoke-pass-1";
int failures = 0;

var client = new LoginNetworkManager();
bool connected = false;
client.onClientConnected = () => connected = true;
if (!client.StartClient(host, port))
{
    Console.WriteLine("FAIL could not start client");
    return 1;
}
await Pump(() => connected, "connect");
Console.WriteLine($"ok   connected to login server {host}:{port}");

var reg = await Ask<ResponseUserRegisterMessage>(cb => client.RequestUserRegister(username, password, "", cb), "register");
Check(reg.code == AckResponseCode.Success, $"register {username}", $"{reg.code} {reg.resp.message}");

var badLogin = await Ask<ResponseUserLoginMessage>(cb => client.RequestUserLogin(username, "wrong-password", cb), "bad login");
Check(badLogin.code != AckResponseCode.Success, "wrong password is refused", $"{badLogin.code} {badLogin.resp.message}");

var login = await Ask<ResponseUserLoginMessage>(cb => client.RequestUserLogin(username, password, cb), "login");
Check(login.code == AckResponseCode.Success && !string.IsNullOrEmpty(login.resp.accessToken), "login", $"{login.code} {login.resp.message} userId={login.resp.userId}");

var token = await Ask<ResponseValidateAccessTokenMessage>(cb => client.RequestValidateAccessToken(login.resp.userId, login.resp.accessToken, cb), "validate token");
Check(token.code == AckResponseCode.Success, "access token validates", $"{token.code} {token.resp.message}");

var channels = await Ask<ResponseChannelsMessage>(cb => client.RequestChannels(cb), "channels");
Check(channels.code == AckResponseCode.Success, "channels",
    $"{channels.code} [{string.Join(", ", (channels.resp.channels ?? new()).Select(c => $"{c.id} {c.connections}/{c.maxConnections}"))}]");

var before = await Ask<ResponseCharactersMessage>(cb => client.RequestCharacters(cb), "characters");
Check(before.code == AckResponseCode.Success && (before.resp.characters?.Count ?? 0) == 0, "characters (new user has none)", $"{before.code} count={before.resp.characters?.Count}");

var newCharacter = new PlayerCharacterData
{
    CharacterName = "Smoke" + DateTime.UtcNow.ToString("mmssfff"),
    DataId = dataId,
    EntityId = entityId,
    FactionId = factionId,
};
var create = await Ask<ResponseCreateCharacterMessage>(cb => client.RequestCreateCharacter(newCharacter, cb), "create character");
Check(create.code == AckResponseCode.Success, $"create character {newCharacter.CharacterName}", $"{create.code} {create.resp.message}");

var after = await Ask<ResponseCharactersMessage>(cb => client.RequestCharacters(cb), "characters");
var made = after.resp.characters?.FirstOrDefault();
Check(after.code == AckResponseCode.Success && (after.resp.characters?.Count ?? 0) == 1, "characters (one now)",
    $"{after.code} count={after.resp.characters?.Count} name={made?.CharacterName} level={made?.Level} map={made?.CurrentMapName} gold={made?.Gold}");
// The server built it from the same creation data, so it starts where that data says
Check(made != null && made.DataId == dataId && made.EntityId == entityId && made.CurrentMapName == template.CurrentMapName,
    "character matches the creation data", $"dataId={made?.DataId} entityId={made?.EntityId} map={made?.CurrentMapName} (expected {dataId} {entityId} {template.CurrentMapName})");

client.StopClient();
Console.WriteLine(failures == 0 ? "PASS all steps" : $"FAIL {failures} step(s)");
return failures == 0 ? 0 : 1;

void Check(bool ok, string step, string detail)
{
    if (!ok) failures++;
    Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {step}: {detail}");
}

async Task Pump(Func<bool> done, string what)
{
    DateTime deadline = DateTime.UtcNow.AddSeconds(10);
    while (!done())
    {
        client.ProcessUpdate();
        if (DateTime.UtcNow > deadline)
            throw new TimeoutException(what);
        await Task.Delay(16);
    }
}

async Task<int> ConnectionLimit(string limitHost, int limitPort, int clients, int expected)
{
    var connections = new List<Connection>();
    int failed = 0;

    // Starts one more client and waits until the server has let it in or turned it away
    async Task<Connection> Open()
    {
        var connection = new Connection();
        connection.Manager.onClientConnected = () => connection.State = "connected";
        connection.Manager.onClientDisconnected = (reason, socketError, message) => connection.State = $"refused ({reason})";
        connections.Add(connection);
        if (!connection.Manager.StartClient(limitHost, limitPort))
        {
            connection.State = "could not start";
            return connection;
        }
        // An unanswered connect gives up by itself after a few seconds, so this ends either way
        DateTime deadline = DateTime.UtcNow.AddSeconds(15);
        while (connection.State == Connection.NoAnswer && DateTime.UtcNow < deadline)
            await PumpAll(0.05);
        return connection;
    }

    // Keeps every client's connection alive
    async Task PumpAll(double seconds)
    {
        DateTime until = DateTime.UtcNow.AddSeconds(seconds);
        do
        {
            foreach (Connection connection in connections)
                connection.Manager.ProcessUpdate();
            await Task.Delay(16);
        } while (DateTime.UtcNow < until);
    }

    void CheckLimit(bool ok, string step, string detail)
    {
        if (!ok) failed++;
        Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {step}: {detail}");
    }

    for (int i = 0; i < clients; ++i)
        await Open();
    // Still connected a moment later, not let in and then dropped
    await PumpAll(1);
    int connected = connections.Count(c => c.State == "connected");
    CheckLimit(connected == expected, $"{expected} of {clients} connections are let in",
        $"{connected} connected [{string.Join(", ", connections.Select(c => c.State))}]");

    if (expected < clients)
    {
        Connection leaving = connections.FirstOrDefault(c => c.State == "connected");
        if (leaving == null)
        {
            CheckLimit(false, "a place frees when a client leaves", "no client was connected");
        }
        else
        {
            leaving.Manager.StopClient();
            connections.Remove(leaving);
            await PumpAll(1);
            Connection late = await Open();
            await PumpAll(1);
            CheckLimit(late.State == "connected", "a place frees when a client leaves", $"the newcomer is {late.State}");
        }
    }

    foreach (Connection connection in connections)
        connection.Manager.StopClient();
    Console.WriteLine(failed == 0 ? "PASS all steps" : $"FAIL {failed} step(s)");
    return failed == 0 ? 0 : 1;
}

async Task<int> MalformedPackets(string targetHost, int targetPort, string connectKey)
{
    // Every packet starts with a message type. After type 1 (a request) the server reads the
    // request's type and id, after type 2 (a response) the id and a result code, and anything
    // else goes to that message's handler, if there is one. Numbers are packed: below 241 a
    // number is one byte, and 250 says three more bytes follow.
    var packets = new List<(string name, byte[][] data)>
    {
        ("an empty packet", new[] { new byte[0] }),
        ("a message type cut off part way", new[] { new byte[] { 250 } }),
        ("a request with nothing after the message type", new[] { new byte[] { 1 } }),
        ("a request with no request id", new[] { new byte[] { 1, (byte)MMORequestTypes.Channels } }),
        ("a request with half a request id", new[] { new byte[] { 1, (byte)MMORequestTypes.Channels, 250, 1 } }),
        ("a request of a type nobody handles", new[] { new byte[] { 1, 240, 7 } }),
        ("a login request with nothing in it", new[] { new byte[] { 1, (byte)MMORequestTypes.UserLogin, 7 } }),
        ("a login request whose name is cut off", new[] { new byte[] { 1, (byte)MMORequestTypes.UserLogin, 7, 255, 255, (byte)'a' } }),
        ("a response with nothing after the message type", new[] { new byte[] { 2 } }),
        ("a response with no result code", new[] { new byte[] { 2, 1 } }),
        ("a response to a request nobody made", new[] { new byte[] { 2, 240, 0 } }),
        // Request and database request types are numbered from 0, so these reach real handlers
        ("every request type with nothing in it", Enumerable.Range(0, 120).Select(type => new byte[] { 1, (byte)type, 9 }).ToArray()),
        ("every message type with nothing after it", Enumerable.Range(0, 32).Select(type => new byte[] { (byte)type }).ToArray()),
        ("every message type followed by 0xFF bytes", Enumerable.Range(3, 29).Select(type => new[] { (byte)type }.Concat(Enumerable.Repeat((byte)255, 64)).ToArray()).ToArray()),
    };
    // The same bytes every run, so a failure can be repeated
    var random = new Random(20261122);
    packets.Add(("300 packets of random bytes", Enumerable.Range(0, 300).Select(_ =>
    {
        byte[] bytes = new byte[random.Next(1, 200)];
        random.NextBytes(bytes);
        return bytes;
    }).ToArray()));
    packets.Add(("300 requests and responses of random bytes", Enumerable.Range(0, 300).Select(i =>
    {
        byte[] bytes = new byte[random.Next(1, 200)];
        random.NextBytes(bytes);
        bytes[0] = (byte)(1 + i % 2);
        return bytes;
    }).ToArray()));
    // More in one frame than a server could be allowed to stop and report one by one
    packets.Add(("5000 cut-off requests at once", Enumerable.Repeat(new byte[] { 1 }, 5000).ToArray()));

    // Connects, sends the packets if there are any, and leaves. False if it was not let in.
    async Task<bool> Visit(byte[][] data)
    {
        var listener = new LiteNetLib.EventBasedNetListener();
        var manager = new LiteNetLib.NetManager(listener);
        LiteNetLib.NetPeer connectedPeer = null;
        bool closed = false;
        listener.PeerConnectedEvent += peer => connectedPeer = peer;
        listener.PeerDisconnectedEvent += (peer, info) => closed = true;
        try
        {
            if (!manager.Start() || manager.Connect(targetHost, targetPort, connectKey) == null)
                return false;
            // An unanswered connect gives up by itself after a few seconds
            DateTime deadline = DateTime.UtcNow.AddSeconds(15);
            while (connectedPeer == null && !closed && DateTime.UtcNow < deadline)
            {
                manager.PollEvents();
                await Task.Delay(16);
            }
            if (connectedPeer == null)
                return false;
            foreach (byte[] bytes in data)
                connectedPeer.Send(bytes, LiteNetLib.DeliveryMethod.ReliableOrdered);
            // Until the server has them all, then a moment for it to read them
            deadline = DateTime.UtcNow.AddSeconds(30);
            DateTime? arrivedAt = null;
            while (!closed && DateTime.UtcNow < deadline)
            {
                manager.PollEvents();
                if (arrivedAt == null && connectedPeer.GetPacketsCountInReliableQueue(0, true) == 0)
                    arrivedAt = DateTime.UtcNow;
                if (arrivedAt != null && DateTime.UtcNow > arrivedAt.Value.AddSeconds(data.Length == 0 ? 0.1 : 0.3))
                    break;
                await Task.Delay(16);
            }
            return true;
        }
        finally
        {
            manager.Stop(true);
        }
    }

    if (!await Visit(new byte[0][]))
    {
        Console.WriteLine($"FAIL no server lets a client in at {targetHost}:{targetPort} with the connect key {connectKey}");
        return 1;
    }
    Console.WriteLine($"ok   connected to {targetHost}:{targetPort}");
    foreach ((string name, byte[][] data) in packets)
    {
        bool sent = await Visit(data);
        // The server lets nobody in once its loop has stopped
        bool up = sent && await Visit(new byte[0][]);
        Console.WriteLine($"{(up ? "ok  " : "FAIL")} {name}: {(up ? "the server is still up" : "the server no longer lets a client in")}");
        if (!up)
        {
            Console.WriteLine("FAIL the server went down");
            return 1;
        }
    }
    Console.WriteLine("PASS all steps");
    return 0;
}

async Task<(AckResponseCode code, T resp)> Ask<T>(Func<ResponseDelegate<T>, bool> send, string what)
    where T : LiteNetLib.Utils.INetSerializable
{
    (AckResponseCode, T)? result = null;
    if (!send((handler, code, resp) => result = (code, resp)))
        throw new Exception(what + ": request was not sent");
    await Pump(() => result.HasValue, what);
    return result.Value;
}

class Connection
{
    public const string NoAnswer = "no answer";
    public readonly LoginNetworkManager Manager = new LoginNetworkManager();
    public string State = NoAnswer;
}
