#!/usr/bin/env python3
"""Run the built .NET hosts in Hosts~ and check that they work.

    dotnet build "Hosts~/OpenMMORPG.Hosts.sln" -c Release
    python "Tools~/hosts_smoke.py" .

Two passes, each on a fresh database:

  litenetlib  the database, central and login servers in one process
  rest        the database web API, with central and login reaching it through RestDatabaseClient

In each pass the smoke client registers, logs in and creates a character, and then the host is
asked to stop (SIGTERM, or Ctrl+Break on Windows). The pass fails unless the smoke client passes,
the host exits with 0, and it reports that the character saves it held were written.

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
STOPPED_LINE = "All character saves are written."


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

    host = start(servers, "OpenMMORPG.MMO.NET", args, os.path.join(work, name, "servers.log"), settings)
    ok = ok and wait_for("servers", lambda: READY_LINE in read(host.log_path), host)
    if ok:
        result = subprocess.run(["dotnet", os.path.join(smoke, "OpenMMORPG.SmokeClient.dll"), "127.0.0.1", str(login_port)],
                                capture_output=True, text=True)
        lines = [line for line in result.stdout.splitlines() if line.startswith(("ok ", "FAIL", "PASS"))]
        print("\n".join(lines))
        if result.returncode != 0:
            print(f"FAIL smoke client exited with {result.returncode}\n{result.stdout}\n{result.stderr}")
            ok = False

    code = stop(host)
    log = read(host.log_path)
    if code != 0:
        print(f"FAIL servers did not stop cleanly (exit code {code})")
        ok = False
    elif name == "litenetlib" and STOPPED_LINE not in log:
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
    parser.add_argument("--only", choices=["litenetlib", "rest"], help="run one pass")
    options = parser.parse_args()

    work = tempfile.mkdtemp(prefix="hosts-smoke-")
    try:
        passes = [options.only] if options.only else ["litenetlib", "rest"]
        results = [run_pass(name, options.repo, options.configuration, work, options.port_offset) for name in passes]
    finally:
        shutil.rmtree(work, ignore_errors=True)
    print("PASS" if all(results) else "FAIL")
    return 0 if all(results) else 1


if __name__ == "__main__":
    sys.exit(main())
