using System.Diagnostics;
using LiteNetLibManager;
using MultiplayerARPG;
using MultiplayerARPG.MMO;

/// <summary>
/// Load test for a running login server: many clients connect and log in at once.
///
/// Usage: OpenMMORPG.SmokeClient bots &lt;host&gt; &lt;loginPort&gt; &lt;bots&gt; [-ramp seconds] [-register]
///   -ramp seconds   start the bots evenly over this long, not all at once
///   -register       each bot registers its account (loadbot0001, ...) before it logs in
///
/// Exit code 0 only if every bot connected and logged in. The login server's flood limits
/// apply to the bots: run them against 127.0.0.1 on the server's machine, or set the limits to 0.
/// </summary>
static class LoginBots
{
    private const string Password = "loadbot-pass-1";

    private class Bot
    {
        public string Username;
        public double StartAt;
        public LoginNetworkManager Manager;
        public bool Started, Connected, LoggedIn, Done;
        public string Failure;
        public double StartedAt, LoginSentAt, ConnectMilliseconds, LoginMilliseconds;
    }

    public static int Run(string[] args)
    {
        if (args.Length < 4 || !int.TryParse(args[2], out int port) || !int.TryParse(args[3], out int count) || count < 1)
        {
            Console.WriteLine("FAIL usage: bots <host> <loginPort> <bots> [-ramp seconds] [-register]");
            return 1;
        }
        string host = args[1];
        double rampSeconds = 0;
        bool register = false;
        for (int i = 4; i < args.Length; ++i)
        {
            if (args[i] == "-register")
                register = true;
            else if (args[i] == "-ramp" && i + 1 < args.Length && double.TryParse(args[i + 1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out rampSeconds))
                ++i;
            else
            {
                Console.WriteLine($"FAIL unknown option {args[i]}");
                return 1;
            }
        }

        List<Bot> bots = new List<Bot>();
        for (int i = 0; i < count; ++i)
            bots.Add(new Bot() { Username = "loadbot" + (i + 1).ToString("D4"), StartAt = rampSeconds * 1000.0 * i / count });

        Stopwatch clock = Stopwatch.StartNew();
        // A connect gives up after 5 seconds and a request after 30, so every bot ends by itself
        double deadline = rampSeconds * 1000.0 + 90000;
        while (bots.Any(b => !b.Done) && clock.Elapsed.TotalMilliseconds < deadline)
        {
            foreach (Bot bot in bots)
            {
                if (!bot.Started && clock.Elapsed.TotalMilliseconds >= bot.StartAt)
                    Start(bot, host, port, clock, register);
                if (bot.Manager == null)
                    continue;
                bot.Manager.ProcessUpdate();
                if (bot.Done)
                {
                    bot.Manager.StopClient();
                    bot.Manager = null;
                }
            }
            // Not an await: the kit's packet writers are shared, so every bot stays on this thread
            Thread.Sleep(1);
        }
        double seconds = clock.Elapsed.TotalSeconds;
        foreach (Bot bot in bots.Where(b => !b.Done))
            Fail(bot, "no answer before the run ended");

        int connected = bots.Count(b => b.Connected);
        int loggedIn = bots.Count(b => b.LoggedIn);
        Console.WriteLine("===== BOT REPORT =====");
        Console.WriteLine($"Bots started     : {count}");
        Console.WriteLine($"Bots connected   : {connected}");
        Console.WriteLine($"Connect rate     : {100.0 * connected / count:0.00}%");
        Console.WriteLine($"Connect time     : {Times(bots.Where(b => b.Connected).Select(b => b.ConnectMilliseconds))}");
        Console.WriteLine("===== LOGIN REPORT =====");
        Console.WriteLine($"Login attempts   : {connected}");
        Console.WriteLine($"Login success    : {loggedIn}");
        Console.WriteLine($"Success rate     : {(connected == 0 ? 0 : 100.0 * loggedIn / connected):0.00}%");
        Console.WriteLine($"Login time       : {Times(bots.Where(b => b.LoggedIn).Select(b => b.LoginMilliseconds))}");
        Console.WriteLine($"Run time         : {seconds:0.0} s");
        foreach (IGrouping<string, Bot> failure in bots.Where(b => b.Failure != null).GroupBy(b => b.Failure).OrderByDescending(g => g.Count()))
            Console.WriteLine($"Failed           : {failure.Count()} {failure.Key}");
        Console.WriteLine(loggedIn == count ? "PASS every bot connected and logged in" : $"FAIL {count - loggedIn} of {count} bots did not get through");
        return loggedIn == count ? 0 : 1;
    }

    private static void Start(Bot bot, string host, int port, Stopwatch clock, bool register)
    {
        LoginNetworkManager manager = new LoginNetworkManager();
        bot.Started = true;
        bot.Manager = manager;
        bot.StartedAt = clock.Elapsed.TotalMilliseconds;

        void Login()
        {
            bot.LoginSentAt = clock.Elapsed.TotalMilliseconds;
            manager.RequestUserLogin(bot.Username, Password, (handler, code, response) =>
            {
                if (code != AckResponseCode.Success)
                {
                    Fail(bot, $"login: {code} {response.message}");
                    return;
                }
                bot.LoginMilliseconds = clock.Elapsed.TotalMilliseconds - bot.LoginSentAt;
                bot.LoggedIn = bot.Done = true;
            });
        }

        manager.onClientConnected = () =>
        {
            bot.Connected = true;
            bot.ConnectMilliseconds = clock.Elapsed.TotalMilliseconds - bot.StartedAt;
            if (!register)
            {
                Login();
                return;
            }
            manager.RequestUserRegister(bot.Username, Password, string.Empty, (handler, code, response) =>
            {
                if (code == AckResponseCode.Success || response.message == UITextKeys.UI_ERROR_USERNAME_EXISTED)
                    Login();
                else
                    Fail(bot, $"register: {code} {response.message}");
            });
        };
        manager.onClientDisconnected = (reason, socketError, message) =>
        {
            if (!bot.Done)
                Fail(bot, $"{(bot.Connected ? "dropped" : "not let in")}: {reason}");
        };
        if (!manager.StartClient(host, port))
            Fail(bot, "could not start a client");
    }

    private static void Fail(Bot bot, string why)
    {
        bot.Failure = why;
        bot.Done = true;
    }

    private static string Times(IEnumerable<double> milliseconds)
    {
        List<double> sorted = milliseconds.OrderBy(m => m).ToList();
        if (sorted.Count == 0)
            return "n/a";
        return $"median {sorted[sorted.Count / 2]:0} ms, slowest {sorted[sorted.Count - 1]:0} ms";
    }
}
