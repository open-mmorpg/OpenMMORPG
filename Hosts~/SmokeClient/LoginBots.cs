using System.Diagnostics;
using LiteNetLib;
using LiteNetLibManager;
using MultiplayerARPG;
using MultiplayerARPG.MMO;

/// <summary>
/// A load test for a running login server: many clients connect and log in at once, and a report
/// says how many got through and how long it took them.
///
/// Usage: OpenMMORPG.SmokeClient bots &lt;host&gt; &lt;loginPort&gt; &lt;bots&gt; [-ramp seconds] [-register]
///        [-characters] [-hold] [-prefix name] [-registrars count]
///
///   -ramp seconds   start the bots evenly over this long. Without it they all start at once.
///   -register       create the bots' accounts first (loadbot0001, loadbot0002, ...). Needed the
///                   first time against a database; it is not part of the timed run.
///   -characters     after logging in, each bot also asks for its character list
///   -hold           bots stay connected until the run ends. Without it each one leaves as soon
///                   as it is done, as a client does when it moves on to a map server.
///   -prefix name    the start of the account names, when "loadbot" will not do
///   -registrars n   how many connections register accounts at the same time (8). Use 1 against
///                   a server that cannot take several registrations at once.
///
/// Exit code 0 only if every bot connected and logged in.
///
/// The login server's flood limits apply to the bots like to anyone else. Run them on the
/// server's own machine against 127.0.0.1 (loopback is exempt from the per-address limits by
/// default), or turn the limits off on the test server by setting them to 0 in serverConfig.json.
/// </summary>
static class LoginBots
{
    private const string Password = "loadbot-pass-1";
    // A connection's request budget is 30 in 10 seconds by default, so a registrar starts a new
    // connection well before that
    private const int RegistrationsPerConnection = 20;
    private const int DefaultRegistrars = 8;

    private enum Stage { Waiting, Connecting, LoggingIn, ListingCharacters, Done, Failed }

    private class Bot
    {
        public string Username;
        public double StartAtMilliseconds;
        public LoginNetworkManager Manager;
        public Stage Stage = Stage.Waiting;
        public bool Connected;
        public bool LoggedIn;
        public bool Leave;
        public string Failure;
        public double StartedAt;
        public double RequestSentAt;
        public double ConnectMilliseconds = -1;
        public double LoginMilliseconds = -1;
        public double CharactersMilliseconds = -1;
    }

    public static int Run(string[] args)
    {
        if (args.Length < 4 || !int.TryParse(args[2], out int port) || !int.TryParse(args[3], out int count) || count < 1)
        {
            Console.WriteLine("FAIL usage: bots <host> <loginPort> <bots> [-ramp seconds] [-register] [-characters] [-hold] [-prefix name] [-registrars count]");
            return 1;
        }
        string host = args[1];
        double rampSeconds = 0;
        string prefix = "loadbot";
        bool register = false, characters = false, hold = false;
        int registrars = DefaultRegistrars;
        for (int i = 4; i < args.Length; ++i)
        {
            switch (args[i])
            {
                case "-ramp" when i + 1 < args.Length && double.TryParse(args[i + 1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double seconds):
                    rampSeconds = Math.Max(0, seconds);
                    ++i;
                    break;
                case "-prefix" when i + 1 < args.Length:
                    prefix = args[++i];
                    break;
                case "-registrars" when i + 1 < args.Length && int.TryParse(args[i + 1], out int registrarCount):
                    registrars = Math.Max(1, registrarCount);
                    ++i;
                    break;
                case "-register":
                    register = true;
                    break;
                case "-characters":
                    characters = true;
                    break;
                case "-hold":
                    hold = true;
                    break;
                default:
                    Console.WriteLine($"FAIL unknown option {args[i]}");
                    return 1;
            }
        }

        List<Bot> bots = new List<Bot>();
        for (int i = 0; i < count; ++i)
        {
            bots.Add(new Bot()
            {
                Username = prefix + (i + 1).ToString("D4"),
                StartAtMilliseconds = rampSeconds * 1000.0 * i / count,
            });
        }

        if (register && !Register(host, port, bots, registrars))
            return 1;

        Console.WriteLine($"Starting {count} bots against {host}:{port}{(rampSeconds > 0 ? $" over {rampSeconds:0.##} seconds" : ", all at once")}");
        Stopwatch clock = Stopwatch.StartNew();
        // A connect that gets no answer gives up after 5 seconds and a request after 30, so
        // every bot ends by itself; the deadline is for whatever does not
        double deadline = rampSeconds * 1000.0 + 90000;
        int unfinished = count;
        double lastProgressAt = 0;
        while (unfinished > 0 && clock.Elapsed.TotalMilliseconds < deadline)
        {
            double now = clock.Elapsed.TotalMilliseconds;
            foreach (Bot bot in bots)
            {
                if (bot.Stage == Stage.Waiting)
                {
                    if (now < bot.StartAtMilliseconds)
                        continue;
                    Start(bot, host, port, clock, characters);
                }
                if (bot.Manager == null)
                    continue;
                try
                {
                    bot.Manager.ProcessUpdate();
                }
                catch (Exception ex)
                {
                    // An answer the client could not read. One bad packet must not end the run
                    if (bot.Stage != Stage.Done && bot.Stage != Stage.Failed)
                        Fail(bot, $"the server sent something unreadable ({ex.GetType().Name})");
                }
                if (bot.Leave && !hold)
                {
                    bot.Leave = false;
                    bot.Manager.StopClient();
                    bot.Manager = null;
                }
            }
            unfinished = bots.Count(b => b.Stage != Stage.Done && b.Stage != Stage.Failed);
            if (now - lastProgressAt >= 5000)
            {
                lastProgressAt = now;
                Console.WriteLine($"  {now / 1000:0} s: {bots.Count(b => b.LoggedIn)} logged in, {bots.Count(b => b.Stage == Stage.Failed)} failed, {unfinished} still going");
            }
            // Not an await: every client in this process shares the kit's packet writers, so the
            // whole run stays on this one thread
            Thread.Sleep(1);
        }
        double runMilliseconds = clock.Elapsed.TotalMilliseconds;
        foreach (Bot bot in bots)
        {
            if (bot.Stage != Stage.Done && bot.Stage != Stage.Failed)
                Fail(bot, bot.Stage == Stage.Waiting ? "never started" : "no answer before the run ended");
            if (bot.Manager != null)
                bot.Manager.StopClient();
        }

        return Report(bots, runMilliseconds, characters);
    }

    private static void Start(Bot bot, string host, int port, Stopwatch clock, bool characters)
    {
        LoginNetworkManager manager = new LoginNetworkManager();
        bot.Manager = manager;
        bot.Stage = Stage.Connecting;
        bot.StartedAt = clock.Elapsed.TotalMilliseconds;
        manager.onClientConnected = () =>
        {
            bot.Connected = true;
            bot.ConnectMilliseconds = clock.Elapsed.TotalMilliseconds - bot.StartedAt;
            bot.Stage = Stage.LoggingIn;
            bot.RequestSentAt = clock.Elapsed.TotalMilliseconds;
            bool sent = manager.RequestUserLogin(bot.Username, Password, (handler, code, response) =>
            {
                if (bot.Stage != Stage.LoggingIn)
                    return;
                if (code != AckResponseCode.Success)
                {
                    Fail(bot, "login " + Describe(code, response.message));
                    return;
                }
                bot.LoggedIn = true;
                bot.LoginMilliseconds = clock.Elapsed.TotalMilliseconds - bot.RequestSentAt;
                if (!characters)
                {
                    Finish(bot);
                    return;
                }
                bot.Stage = Stage.ListingCharacters;
                bot.RequestSentAt = clock.Elapsed.TotalMilliseconds;
                bool asked = manager.RequestCharacters((charactersHandler, charactersCode, charactersResponse) =>
                {
                    if (bot.Stage != Stage.ListingCharacters)
                        return;
                    if (charactersCode != AckResponseCode.Success)
                    {
                        Fail(bot, "character list " + Describe(charactersCode, charactersResponse.message));
                        return;
                    }
                    bot.CharactersMilliseconds = clock.Elapsed.TotalMilliseconds - bot.RequestSentAt;
                    Finish(bot);
                });
                if (!asked)
                    Fail(bot, "character list request was not sent");
            });
            if (!sent)
                Fail(bot, "login request was not sent");
        };
        manager.onClientDisconnected = (reason, socketError, message) =>
        {
            if (bot.Stage == Stage.Done || bot.Stage == Stage.Failed)
                return;
            string what = bot.Stage == Stage.Connecting ? "not let in" : "dropped while " + (bot.Stage == Stage.LoggingIn ? "logging in" : "listing characters");
            Fail(bot, $"{what} ({reason}{(message != UITextKeys.NONE ? ", " + message : string.Empty)})");
        };
        if (!manager.StartClient(host, port))
            Fail(bot, "could not start a client");
    }

    private static void Finish(Bot bot)
    {
        bot.Stage = Stage.Done;
        bot.Leave = true;
    }

    private static void Fail(Bot bot, string why)
    {
        bot.Stage = Stage.Failed;
        bot.Failure = why;
        bot.Leave = true;
    }

    private static string Describe(AckResponseCode code, UITextKeys message)
    {
        if (code == AckResponseCode.Timeout)
            return "got no answer (Timeout)";
        return $"refused ({(message != UITextKeys.NONE ? message.ToString() : code.ToString())})";
    }

    private class Registrar
    {
        public LoginNetworkManager Manager;
        public bool Connected;
        public bool Closed;
        public int Sent;
        public Bot Current;
        public (AckResponseCode code, UITextKeys message)? Answer;
        public DateTime Deadline;
    }

    /// <summary>
    /// Creates the accounts that do not exist yet. A few connections work through the names,
    /// one request at a time each, and start over on a new connection now and then to stay
    /// inside a connection's request budget.
    /// </summary>
    private static bool Register(string host, int port, List<Bot> bots, int registrarCount)
    {
        Console.WriteLine($"Registering {bots.Count} accounts ({bots[0].Username} to {bots[bots.Count - 1].Username})");
        Queue<Bot> waiting = new Queue<Bot>(bots);
        int created = 0, existed = 0;
        string failure = null;
        List<Registrar> registrars = Enumerable.Range(0, registrarCount).Select(_ => new Registrar()).ToList();
        while (failure == null && (waiting.Count > 0 || registrars.Any(r => r.Manager != null)))
        {
            foreach (Registrar registrar in registrars)
            {
                if (registrar.Manager == null)
                {
                    if (waiting.Count == 0)
                        continue;
                    Registrar opened = registrar;
                    opened.Manager = new LoginNetworkManager();
                    opened.Connected = opened.Closed = false;
                    opened.Sent = 0;
                    opened.Manager.onClientConnected = () => opened.Connected = true;
                    opened.Manager.onClientDisconnected = (reason, socketError, message) => opened.Closed = true;
                    opened.Deadline = DateTime.UtcNow.AddSeconds(15);
                    if (!opened.Manager.StartClient(host, port))
                    {
                        failure = "could not start a client";
                        break;
                    }
                }
                registrar.Manager.ProcessUpdate();
                if (!registrar.Connected)
                {
                    if (registrar.Closed || DateTime.UtcNow > registrar.Deadline)
                        failure = "the login server did not let a client in";
                }
                else if (registrar.Current == null)
                {
                    if (waiting.Count == 0 || registrar.Sent >= RegistrationsPerConnection)
                    {
                        registrar.Manager.StopClient();
                        registrar.Manager = null;
                        continue;
                    }
                    Registrar asking = registrar;
                    asking.Current = waiting.Dequeue();
                    asking.Answer = null;
                    asking.Sent++;
                    asking.Deadline = DateTime.UtcNow.AddSeconds(40);
                    asking.Manager.RequestUserRegister(asking.Current.Username, Password, string.Empty, (handler, code, response) => asking.Answer = (code, response.message));
                }
                else if (registrar.Answer != null)
                {
                    if (registrar.Answer.Value.code == AckResponseCode.Success)
                        ++created;
                    else if (registrar.Answer.Value.message == UITextKeys.UI_ERROR_USERNAME_EXISTED)
                        ++existed;
                    else
                        failure = $"{registrar.Current.Username}: {Describe(registrar.Answer.Value.code, registrar.Answer.Value.message)}";
                    registrar.Current = null;
                }
                else if (registrar.Closed || DateTime.UtcNow > registrar.Deadline)
                {
                    failure = $"{registrar.Current.Username}: no answer";
                }
                if (failure != null)
                    break;
            }
            Thread.Sleep(1);
        }
        foreach (Registrar registrar in registrars)
        {
            if (registrar.Manager != null)
                registrar.Manager.StopClient();
        }
        if (failure != null)
        {
            Console.WriteLine($"FAIL registering the accounts: {failure}");
            if (failure.Contains(UITextKeys.UI_ERROR_TOO_MANY_REQUESTS.ToString()))
                Console.WriteLine("     The login server's flood limits apply to this address. Run the bots on the server's machine against 127.0.0.1, or set the register limits to 0 on the test server.");
            return false;
        }
        Console.WriteLine($"ok   accounts: {created} created, {existed} were there already");
        return true;
    }

    private static int Report(List<Bot> bots, double runMilliseconds, bool characters)
    {
        int started = bots.Count;
        int connected = bots.Count(b => b.Connected);
        int loggedIn = bots.Count(b => b.LoggedIn);
        int listed = bots.Count(b => b.CharactersMilliseconds >= 0);
        int done = bots.Count(b => b.Stage == Stage.Done);

        Console.WriteLine();
        Console.WriteLine("===== BOT REPORT =====");
        Line("Bots started", started.ToString());
        Line("Bots connected", connected.ToString());
        Line("Bots not connected", (started - connected).ToString());
        Line("Connect rate", Rate(connected, started));
        Line("Connect time", Times(bots.Where(b => b.ConnectMilliseconds >= 0).Select(b => b.ConnectMilliseconds)));
        Console.WriteLine();
        Console.WriteLine("===== LOGIN REPORT =====");
        Line("Login attempts", connected.ToString());
        Line("Login success", loggedIn.ToString());
        Line("Login failed", (connected - loggedIn).ToString());
        Line("Success rate", Rate(loggedIn, connected));
        Line("Login time", Times(bots.Where(b => b.LoginMilliseconds >= 0).Select(b => b.LoginMilliseconds)));
        if (characters)
        {
            Console.WriteLine();
            Console.WriteLine("===== CHARACTER LIST REPORT =====");
            Line("Lists asked for", loggedIn.ToString());
            Line("Lists received", listed.ToString());
            Line("Success rate", Rate(listed, loggedIn));
            Line("List time", Times(bots.Where(b => b.CharactersMilliseconds >= 0).Select(b => b.CharactersMilliseconds)));
        }
        Console.WriteLine();
        Console.WriteLine("===== WHOLE RUN =====");
        Line("Bots that got through", $"{done} of {started} ({Rate(done, started)})");
        Line("Run time", $"{runMilliseconds / 1000:0.0} s");
        if (loggedIn > 0)
            Line("Logins per second", $"{loggedIn / (runMilliseconds / 1000):0.0}");
        List<IGrouping<string, Bot>> failures = bots.Where(b => b.Failure != null).GroupBy(b => b.Failure).OrderByDescending(g => g.Count()).ToList();
        if (failures.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("===== FAILURES =====");
            foreach (IGrouping<string, Bot> failure in failures)
                Line(failure.Count().ToString(), failure.Key);
            if (failures.Any(f => f.Key.Contains(UITextKeys.UI_ERROR_INVALID_USERNAME_OR_PASSWORD.ToString())))
                Console.WriteLine("The accounts may not exist yet: run once with -register.");
            if (failures.Any(f => f.Key.Contains(UITextKeys.UI_ERROR_TOO_MANY_REQUESTS.ToString())))
                Console.WriteLine("The login server's flood limits turned bots away: run them against 127.0.0.1 on the server's machine, or set the limits to 0 on the test server.");
        }
        Console.WriteLine("======================");
        bool passed = done == started;
        Console.WriteLine(passed ? "PASS every bot connected and logged in" : $"FAIL {started - done} of {started} bots did not get through");
        return passed ? 0 : 1;
    }

    private static void Line(string name, string value)
    {
        Console.WriteLine($"{name,-22}: {value}");
    }

    private static string Rate(int part, int whole)
    {
        return whole == 0 ? "n/a" : $"{100.0 * part / whole:0.00}%";
    }

    private static string Times(IEnumerable<double> milliseconds)
    {
        List<double> sorted = milliseconds.OrderBy(m => m).ToList();
        if (sorted.Count == 0)
            return "n/a";
        double At(double fraction) => sorted[Math.Min(sorted.Count - 1, (int)(fraction * sorted.Count))];
        return $"median {At(0.5):0} ms, 95% within {At(0.95):0} ms, slowest {sorted[sorted.Count - 1]:0} ms";
    }
}
