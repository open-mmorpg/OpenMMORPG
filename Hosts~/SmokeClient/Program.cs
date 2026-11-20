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

async Task<(AckResponseCode code, T resp)> Ask<T>(Func<ResponseDelegate<T>, bool> send, string what)
    where T : LiteNetLib.Utils.INetSerializable
{
    (AckResponseCode, T)? result = null;
    if (!send((handler, code, resp) => result = (code, resp)))
        throw new Exception(what + ": request was not sent");
    await Pump(() => result.HasValue, what);
    return result.Value;
}
