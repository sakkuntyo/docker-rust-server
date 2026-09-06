#!/usr/bin/env python3
"""Keep a season's terrain and map URLs outside Rust's cleanup/backup rotation."""

import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import selectors
import shutil
import signal
import subprocess
import sys
import tempfile
import time
import uuid
from datetime import datetime, timezone


class MapError(RuntimeError):
    pass


def now():
    return datetime.now(timezone.utc).isoformat()


def regular(path):
    if path.is_symlink() or not path.is_file() or path.stat().st_size == 0:
        raise MapError(f"Expected a non-empty regular file: {path}")
    return path


def digest(path):
    with regular(path).open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def atomic_copy(source, target):
    regular(source)
    target.parent.mkdir(parents=True, exist_ok=True)
    fd, temporary = tempfile.mkstemp(prefix=".map-copy-", dir=target.parent)
    try:
        with os.fdopen(fd, "wb") as output, source.open("rb") as input_file:
            shutil.copyfileobj(input_file, output)
            output.flush()
            os.fsync(output.fileno())
        os.replace(temporary, target)  # Replace a legacy link, never its target.
    finally:
        Path(temporary).unlink(missing_ok=True)


def atomic_json(target, value):
    target.parent.mkdir(parents=True, exist_ok=True)
    fd, temporary = tempfile.mkstemp(prefix=".map-json-", dir=target.parent)
    try:
        with os.fdopen(fd, "w", encoding="utf-8") as output:
            json.dump(value, output, ensure_ascii=False, indent=2)
            output.write("\n")
            output.flush()
            os.fsync(output.fileno())
        os.replace(temporary, target)
    finally:
        Path(temporary).unlink(missing_ok=True)


def save_protocol(assembly):
    # Parse metadata only: never execute server code to discover the save number.
    import dnfile

    with dnfile.dnPE(str(assembly)) as pe:
        fields = []
        for row in pe.net.mdtables.TypeDef:
            if str(row.TypeNamespace) == "Rust" and str(row.TypeName) == "Protocol":
                fields.extend(f.row for f in row.FieldList if str(f.row.Name) == "save")
        values = [int.from_bytes(c.Value.value, "little")
                  for c in pe.net.mdtables.Constant
                  if c.Parent.row in fields and c.Type in (8, 9)]
    if len(values) != 1 or not 0 < values[0] < 1000000:
        raise MapError("Cannot determine Rust.Protocol.save; refusing to guess")
    return values[0]


class Season:
    def __init__(self, root):
        self.root = Path(root).absolute()
        if self.root.is_symlink() or self.root.name != "server":
            raise MapError("The data directory must be a real directory named server")
        self.root.mkdir(parents=True, exist_ok=True)
        self.identity = self.root / "serverdata1"
        self.history = self.root / ".map-history"
        for path in (self.identity, self.history):
            if path.is_symlink() or (path.exists() and not path.is_dir()):
                raise MapError(f"Unsafe directory: {path}")
        self.history.mkdir(exist_ok=True)
        self.active = self.root / "map-season.json"
        if self.active.is_symlink():
            raise MapError("map-season.json must not be a symbolic link")
        self.state = json.loads(self.active.read_text()) if self.active.exists() else None
        if self.state:
            if (self.state.get("schema") != 1 or
                    not re.fullmatch(r"[0-9a-f]{32}", self.state.get("id", ""))):
                raise MapError("Invalid season metadata")
            for key in ("seed", "size", "protocol"):
                if type(self.state.get(key)) is not int or self.state[key] < 0:
                    raise MapError(f"Invalid season {key}")
            if self.archive.is_symlink():
                raise MapError("Season archive must not be a symbolic link")

    @property
    def archive(self):
        return self.history / self.state["id"]

    @property
    def prefix(self):
        return f"proceduralmap.{self.state['size']}.{self.state['seed']}"

    def file(self, protocol, extension):
        return self.identity / f"{self.prefix}.{protocol}.{extension}"

    def store(self):
        atomic_json(self.archive / "season.json", self.state)
        atomic_json(self.active, self.state)

    def save_source(self, path):
        resolved = path.resolve(strict=True)
        if resolved.parent != self.identity.resolve():
            raise MapError(f"Save link escapes serverdata1: {path}")
        regular(resolved)
        with resolved.open("rb") as stream:
            if stream.read(4) != b"SAVR":
                raise MapError(f"Invalid save header: {path}")
        return resolved

    def snapshot(self, label):
        target = (self.archive if self.state else self.history) / f"{label}-{uuid.uuid4().hex}"
        if self.identity.exists():
            shutil.copytree(self.identity, target, symlinks=True)
        return target

    def capture_map(self, map_file):
        checksum = digest(map_file)
        original = self.archive / "original.map"
        if self.state.get("sha256"):
            if digest(original) != self.state["sha256"]:
                raise MapError("The archived season map is damaged")
            if checksum != self.state["sha256"]:
                raise MapError("Rust changed the terrain during startup")
        else:
            atomic_copy(map_file, original)
            self.state.update(sha256=checksum, original_protocol=self.state["protocol"])
            self.store()
        return checksum

    def prepare(self, size, seed, protocol, adopt_file=None, adopt_hash=None):
        if not 0 <= seed <= 4294967295 or size <= 0 or protocol <= 0:
            raise MapError("Invalid size, seed or protocol")
        self.identity.mkdir(exist_ok=True)
        source = None
        if not self.state:
            candidates = list(self.identity.glob(f"proceduralmap.{size}.{seed}.*.sav"))
            if candidates and not (adopt_file and adopt_hash):
                raise MapError("Existing world needs ENV_MAP_ADOPT_FILE and ENV_MAP_ADOPT_SHA256 once; verify its original terrain first")
            if adopt_file or adopt_hash:
                if not (adopt_file and re.fullmatch(r"[0-9a-f]{64}", adopt_hash or "")):
                    raise MapError("Both adoption file and SHA-256 are required")
                map_file = Path(adopt_file).absolute()
                if (map_file.parent != self.identity or
                        not re.fullmatch(rf"proceduralmap\.{size}\.{seed}\.[0-9]+\.map", map_file.name)):
                    raise MapError("Adoption map must belong to this seed and size in serverdata1")
                if digest(map_file) != adopt_hash:
                    raise MapError("Adoption map SHA-256 does not match")
                sources = {self.save_source(p) for p in candidates}
                if len(sources) != 1:
                    raise MapError("Cannot identify one current save; resolve ambiguous saves before adoption")
                source = sources.pop()
            self.state = dict(schema=1, id=uuid.uuid4().hex, size=size, seed=seed,
                              protocol=protocol, created_at=now(), ready=False, sha256=None)
            self.archive.mkdir()
            if source:
                self.snapshot("before-adoption")
                self.capture_map(map_file)
            self.store()
        else:
            if self.state.get("failure"):
                raise MapError("Previous startup failed; inspect season.json and its checkpoint before clearing the failure")
            if (self.state["size"], self.state["seed"]) != (size, seed):
                raise MapError("Seed/size changed without an intentional wipe")
            previous = self.file(self.state["protocol"], "sav")
            if previous.exists() or previous.is_symlink():
                source = self.save_source(previous)
                if (not self.state.get("ready") and self.state.get("expected_save") and
                        digest(source) != self.state.get("before_start_save_sha256")):
                    raise MapError("An incomplete startup changed the save; recover from before-start.sav before retrying")
            elif self.state.get("ready") or self.state.get("expected_save"):
                raise MapError("The current season save is missing; refusing to create an empty world")
            if not self.state.get("sha256") and self.state["protocol"] != protocol:
                raise MapError("Protocol changed before the season original was captured")

        destination = self.file(protocol, "sav")
        if source:
            # A regular newer save is ambiguous. Known legacy aliases may be replaced.
            if (destination.exists() and not destination.is_symlink() and
                    destination != source and digest(destination) != digest(source)):
                raise MapError("A different save already exists at the target protocol")
            if self.state["protocol"] != protocol:
                self.snapshot(f"before-protocol-{protocol}")
            atomic_copy(source, self.archive / "before-start.sav")
            self.state["before_start_save_sha256"] = digest(source)
        original = self.archive / "original.map"
        if self.state.get("sha256"):
            if digest(original) != self.state["sha256"]:
                raise MapError("The archived season map is damaged")
            target_map = self.file(protocol, "map")
            if target_map.exists() and not target_map.is_symlink() and digest(target_map) != self.state["sha256"]:
                atomic_copy(target_map, self.archive / f"replaced-{uuid.uuid4().hex}.map")
            atomic_copy(original, target_map)
        if source and (destination != source or destination.is_symlink()):
            atomic_copy(source, destination)
        self.state.update(protocol=protocol, expected_save=source is not None,
                          started_at=now(), ready=False)
        self.store()

    def record_url(self, line):
        match = re.search(r"\[Rust\.MapCache\] Map uploaded to backend: "
                          r"(https://files\.facepunch\.com/rust/maps/([0-9a-f]{64})/[^\s/?#]+\.map)(?:\s|$)", line)
        if not match:
            return
        url, checksum = match.groups()
        map_file = self.file(self.state["protocol"], "map")
        verified = digest(map_file) == checksum
        event = dict(recorded_at=now(), season=self.state["id"], seed=self.state["seed"],
                     size=self.state["size"], protocol=self.state["protocol"],
                     sha256=checksum, verified=verified, url=url)
        journal = self.history / "map-urls.jsonl"
        # fsync each event; retain repeated observations and old seasons as evidence.
        if journal.is_symlink():
            raise MapError("URL journal must not be a symbolic link")
        with journal.open("a", encoding="utf-8") as output:
            output.write(json.dumps(event, ensure_ascii=False) + "\n")
            output.flush()
            os.fsync(output.fileno())
        if not verified:
            raise MapError("Uploaded map hash differs from the local map")
        self.capture_map(map_file)
        self.state.setdefault("original_url", url)
        self.state["last_url"] = url
        self.store()

    def wipe(self, mode):
        # Invoked only when launch.sh's configured wipe deadline has elapsed.
        self.snapshot("before-wipe")
        if mode == "FULL":
            paths = [p for p in self.root.iterdir() if p.name != ".map-history"]
        else:
            paths = [p for p in self.identity.glob("proceduralmap.*")
                     if re.fullmatch(r"proceduralmap\.[0-9]+\.[0-9]+\.[0-9]+\.(map|sav(?:\.[0-9]+)?|dat)", p.name)]
            paths += [self.root / n for n in ("seed", "wipeunixtime", "map-season.json", "createdServerVersion")]
        for path in paths:
            if path.is_symlink() or path.is_file():
                path.unlink()
            elif path.is_dir():
                shutil.rmtree(path)
        self.state = None


FATAL_STARTUP = re.compile(
    r"Invalid save|Error loading save|Skipping entity|Couldn't (?:create|find) prefab|"
    r"Prefab.*(?:mismatch|not found|don't match|doesn't match)|World file.*(?:mismatch|outdated)|"
    r"World cache.*(?:mismatch|outdated)|Failed to load (?:map|world)|"
    r"Error loading (?:map|world)", re.IGNORECASE)


def run_server(season, command, timeout):
    if not season.state:
        raise MapError("prepare must run before the server")
    loaded_save = False
    ready = False
    deadline = time.monotonic() + timeout
    child = subprocess.Popen(command, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    old_handler = signal.signal(signal.SIGTERM, lambda *_: child.terminate())
    pending = b""
    try:
        with selectors.DefaultSelector() as selector:
            selector.register(child.stdout, selectors.EVENT_READ)
            while selector.get_map():
                if not ready and time.monotonic() > deadline:
                    raise MapError("Rust startup timed out")
                for key, _ in selector.select(1):
                    chunk = os.read(key.fd, 65536)
                    if not chunk:
                        selector.unregister(key.fd)
                        continue
                    sys.stdout.buffer.write(chunk)
                    sys.stdout.buffer.flush()
                    pending += chunk
                    while b"\n" in pending:
                        raw, pending = pending.split(b"\n", 1)
                        line = raw.decode("utf-8", errors="replace").strip()
                        season.record_url(line)
                        if ready:
                            continue
                        if FATAL_STARTUP.search(line):
                            raise MapError(f"Incompatible world/save: {line}")
                        if season.state["expected_save"] and "Couldn't load " in line and ".sav" in line:
                            raise MapError("Rust did not load the expected save")
                        if re.fullmatch(r"Spawning [0-9]+ entities from save", line):
                            loaded_save = True
                        if line == "Server startup complete":
                            if season.state["expected_save"] and not loaded_save:
                                raise MapError("Startup completed without confirming the saved entities")
                            season.capture_map(season.file(season.state["protocol"], "map"))
                            season.state.update(ready=True, ready_at=now())
                            season.store()
                            ready = True
                    if len(pending) > 1048576:
                        raise MapError("Unexpected oversized server log line")
        result = child.wait()
        if not ready:
            raise MapError(f"Rust exited before startup completed ({result})")
        return result
    except BaseException as error:
        # Do not ask a partially loaded world to save over the last good save.
        if child.poll() is None:
            child.kill()
        child.wait()
        season.state["failure"] = str(error)
        season.store()
        raise
    finally:
        signal.signal(signal.SIGTERM, old_handler)
        child.stdout.close()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", default="/root/rustserver/server")
    sub = parser.add_subparsers(dest="action", required=True)
    protocol = sub.add_parser("protocol")
    protocol.add_argument("assembly", type=Path)
    prepare = sub.add_parser("prepare")
    prepare.add_argument("--size", type=int, required=True)
    prepare.add_argument("--seed", type=int, required=True)
    prepare.add_argument("--protocol", type=int, required=True)
    prepare.add_argument("--adopt-file", default=os.environ.get("ENV_MAP_ADOPT_FILE"))
    prepare.add_argument("--adopt-hash", default=os.environ.get("ENV_MAP_ADOPT_SHA256"))
    wipe = sub.add_parser("wipe")
    wipe.add_argument("--mode", choices=("FULL", "MAP"), required=True)
    run = sub.add_parser("run")
    run.add_argument("--timeout", type=int, default=1800)
    run.add_argument("command", nargs=argparse.REMAINDER)
    args = parser.parse_args()
    if args.action == "protocol":
        print(save_protocol(args.assembly))
        return 0
    season = Season(args.root)
    if args.action == "prepare":
        season.prepare(args.size, args.seed, args.protocol, args.adopt_file, args.adopt_hash)
    elif args.action == "wipe":
        season.wipe(args.mode)
    else:
        command = args.command[1:] if args.command[:1] == ["--"] else args.command
        if not command or args.timeout <= 0:
            raise MapError("A server command and positive timeout are required")
        return run_server(season, command, args.timeout)
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception as error:
        print(f"ERROR: map preservation: {error}", file=sys.stderr)
        sys.exit(1)
