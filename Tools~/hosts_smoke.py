#!/usr/bin/env python3
"""Run the built .NET hosts in Hosts~ and check that they work.

    dotnet build "Hosts~/OpenMMORPG.Hosts.sln" -c Release
    python "Tools~/hosts_smoke.py" .

Four passes, each on a fresh database:

  litenetlib        the database, central and login servers in one process
  rest              the database web API, with central and login reaching it through RestDatabaseClient
  connection-limit  the login server started with -loginMaxConnections
  secret            as litenetlib, then other processes try to join

Every host gets a serverSecret, as a server outside the editor will not start without one.
In the first two and the last the smoke client registers, logs in and creates a character. In the
third it opens more connections than the login server was told to take, and checks that the extra
ones are refused and that a place frees when a client leaves. Then the host is asked to stop
(SIGTERM, or Ctrl+Break on Windows). A pass fails unless the smoke client passes, the host exits
with 0, and (every pass that runs the database server) it reports that the character saves it held
were written.

The secret pass also starts servers in other processes against the first one's cluster and
database ports: one with the same secret, which must be accepted, one with another secret, which
must be refused, and ones with none or a short one, which must not start.

Needs no Unity. The checks workflow runs it on every push.
"""
import argparse
import os
import shutil
import signal
import socket
import subprocess
import sys
import tempfile
import time

WINDOWS = os.name == "nt"
READY_LINE = "App Registered successfully"      # the login server has joined the cluster
CONNECTION_LIMIT = 2                            # -loginMaxConnections in the connection-limit pass
STOPPED_LINE = "All character saves are written."
SECRET = "hosts-smoke-secret-5f2c9a1e77"
DATABASE_ACCEPTED_LINE = "The database server accepted this server"
DATABASE_REFUSED_LINE = "The database server did not accept this server"
CLUSTER_REFUSED_LINE = "The cluster server refused this server"
NOT_STARTING_LINE = "Not starting: "


def built(repo, project, assembly, configuration):
    folder = os.path.join(repo, "Hosts~", project, "bin", configuration)
    if os.path.isdir(folder):
        for framework in sorted(os.listdir(folder), reverse=True):
            if os.path.isfile(os.path.join(folder, framework, assembly + ".dll")):
                return os.path.join(folder, framework)
    sys.exit(f"{assembly} is not built: run dotnet build \"Hosts~/OpenMMORPG.Hosts.sln\" -c {configuration}")


def start(folder, assembly, args, log_path, settings=None):
    log = open(log_path, "w")
    flags = subprocess.CREATE_NEW_PROCESS_GROUP if WINDOWS else 0
    # The hosts read any serverConfig.json setting from an environment variable of the same name
    process = subprocess.Popen(["dotnet", assembly + ".dll"] + args, cwd=folder, stdout=log,
                               stderr=subprocess.STDOUT, creationflags=flags,
                               env=dict(os.environ, **(settings or {})))
    process.log, process.log_path = log, log_path
    return process


def stop(process, timeout=60):
    """Asks the process to stop, as a service manager would. Returns its exit code, or None."""
    if process.poll() is None:
        process.send_signal(signal.CTRL_BREAK_EVENT if WINDOWS else signal.SIGTERM)
        try:
            process.wait(timeout)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait()
            process.log.close()
            return None
    process.log.close()
    return process.returncode


def read(path):
    with open(path, errors="replace") as log:
        return log.read()


def wait_for(what, condition, process, timeout=60):
    deadline = time.time() + timeout
    while time.time() < deadline:
        if condition():
            return True
        if process.poll() is not None:
            print(f"FAIL {what}: the process ended with {process.returncode}")
            return False
        time.sleep(0.25)
    print(f"FAIL {what}: not within {timeout} seconds")
    return False


def listening(port):
    try:
        with socket.create_connection(("127.0.0.1", port), timeout=1):
            return True
    except OSError:
        return False


def outsider(what, servers, work, args, settings, expected, unexpected=None):
    """Starts another server process against the running one and waits for a line in its log."""
    log_path = os.path.join(work, "secret", what.replace(" ", "-") + ".log")
    process = start(servers, "OpenMMORPG.MMO.NET", args, log_path, settings)
    ok = wait_for(what, lambda: expected in read(log_path), process, timeout=30)
    stop(process)
    log = read(log_path)
    if ok and unexpected and unexpected in log:
        print(f"FAIL {what}: its log has \"{unexpected}\"")
        ok = False
    if ok:
        print(f"ok   {what}")
    else:
        print(f"-- {log_path}\n{log}")
    return ok


def run_outsiders(servers, work, offset):
    """Other processes try the secret pass's cluster and database ports."""
    # A central server of its own, using the first process's database server
    database_client = ["-startCentralServer", "-centralPort", str(7100 + offset),
                       "-clusterPort", str(6110 + offset), "-databaseManagerPort", str(6100 + offset)]
    # A map spawn server joining the first process's cluster. It is only ever refused here:
    # an accepted one would start map servers
    spawner = ["-startMapSpawnServer", "-mapSpawnPort", str(6101 + offset), "-clusterPort", str(6010 + offset)]
    other = {"serverSecret": "not-" + SECRET}
    results = [
        outsider("a server with the secret uses the database", servers, work, database_client,
                 {"serverSecret": SECRET}, DATABASE_ACCEPTED_LINE, DATABASE_REFUSED_LINE),
        outsider("a server with another secret is refused by the database", servers, work, database_client,
                 other, DATABASE_REFUSED_LINE, DATABASE_ACCEPTED_LINE),
        outsider("a server with no secret does not start", servers, work, database_client,
                 {}, NOT_STARTING_LINE + "No `serverSecret` is set", DATABASE_ACCEPTED_LINE),
        outsider("a server with a short secret does not start", servers, work, database_client,
                 {"serverSecret": "hunter2"}, NOT_STARTING_LINE + "`serverSecret` is only 7 characters", DATABASE_ACCEPTED_LINE),
        outsider("a server with another secret is refused by the cluster", servers, work, spawner,
                 other, CLUSTER_REFUSED_LINE, READY_LINE),
    ]
    return all(results)


def run_pass(name, repo, configuration, work, offset):
    print(f"== {name}")
    ok = True
    servers = os.path.join(work, name, "servers")
    shutil.copytree(built(repo, "MMO.NET", "OpenMMORPG.MMO.NET", configuration), servers)
    smoke = built(repo, "SmokeClient", "OpenMMORPG.SmokeClient", configuration)
    login_port = 7500 + offset
    args = ["-startCentralServer", "-startLoginServer",
            "-loginPort", str(login_port), "-centralPort", str(7000 + offset),
            "-clusterPort", str(6010 + offset), "-databaseManagerPort", str(6100 + offset)]
    api = None
    settings = {}
    client_args = ["127.0.0.1", str(login_port)]
    if name == "connection-limit":
        # Two more clients than the login server takes
        args += ["-loginMaxConnections", str(CONNECTION_LIMIT)]
        client_args = ["connections", "127.0.0.1", str(login_port), str(CONNECTION_LIMIT + 2), str(CONNECTION_LIMIT)]
    if name == "rest":
        api_folder = os.path.join(work, name, "database-api")
        shutil.copytree(built(repo, "DatabaseManager", "OpenMMORPG.DatabaseManager", configuration), api_folder)
        api_port = 5757 + offset
        api = start(api_folder, "OpenMMORPG.DatabaseManager", ["--Urls", f"http://localhost:{api_port}"],
                    os.path.join(work, name, "database-api.log"))
        ok = wait_for("database web API", lambda: listening(api_port), api)
        # No leading dash on this one (Server/Consts/ProcessArguments.cs)
        args += ["useCustomDatabaseClient"]
        settings["dbApiUrl"] = f"http://localhost:{api_port}/api/"
    else:
        args += ["-startDatabaseServer"]
    settings["serverSecret"] = SECRET

    host = start(servers, "OpenMMORPG.MMO.NET", args, os.path.join(work, name, "servers.log"), settings)
    ok = ok and wait_for("servers", lambda: READY_LINE in read(host.log_path), host)
    if ok:
        result = subprocess.run(["dotnet", os.path.join(smoke, "OpenMMORPG.SmokeClient.dll")] + client_args,
                                capture_output=True, text=True)
        lines = [line for line in result.stdout.splitlines() if line.startswith(("ok ", "FAIL", "PASS"))]
        print("\n".join(lines))
        if result.returncode != 0:
            print(f"FAIL smoke client exited with {result.returncode}\n{result.stdout}\n{result.stderr}")
            ok = False
    if ok and name == "secret":
        ok = run_outsiders(servers, work, offset)

    code = stop(host)
    log = read(host.log_path)
    if code != 0:
        print(f"FAIL servers did not stop cleanly (exit code {code})")
        ok = False
    elif name != "rest" and STOPPED_LINE not in log:
        print("FAIL the database server did not report its character saves written")
        ok = False
    else:
        print("ok   servers stopped cleanly")
    if api is not None:
        stop(api)
    if not ok:
        print(f"-- {host.log_path}\n{log}")
        if api is not None:
            print(f"-- {api.log_path}\n{read(api.log_path)}")
    return ok


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("repo", help="the kit's root folder (the one that holds Hosts~)")
    parser.add_argument("--configuration", default="Release")
    parser.add_argument("--port-offset", type=int, default=0,
                        help="added to every default port, when the defaults are in use on this machine")
    parser.add_argument("--only", choices=["litenetlib", "rest", "connection-limit", "secret"], help="run one pass")
    options = parser.parse_args()

    work = tempfile.mkdtemp(prefix="hosts-smoke-")
    try:
        passes = [options.only] if options.only else ["litenetlib", "rest", "connection-limit", "secret"]
        results = [run_pass(name, options.repo, options.configuration, work, options.port_offset) for name in passes]
    finally:
        shutil.rmtree(work, ignore_errors=True)
    print("PASS" if all(results) else "FAIL")
    return 0 if all(results) else 1


if __name__ == "__main__":
    sys.exit(main())
