"""Read-only Docker/Rust collector. Stream to `ssh target python3 -`.

No files are installed on the host. RCON passwords stay inside their containers.
REQUEST may be supplied by the desktop transport before this source is executed.
"""
import csv
import base64
import datetime as dt
import json
import re
import subprocess
import sys
from urllib.parse import urlparse

if 'read_connections' not in globals() and '__file__' in globals() and __file__ != '<stdin>':
    from conntrack_reader import read_connections, read_ip_route, read_presence

UTC = dt.timezone.utc
REQUEST = globals().get("REQUEST", {"mode": "overview"})
CONTAINER = re.compile(r"rust-[a-zA-Z0-9][a-zA-Z0-9_.-]*\Z")


def utc(value):
    return dt.datetime.fromtimestamp(float(value), UTC).isoformat().replace("+00:00", "Z")


def stamp(value):
    try:
        # Docker uses nanosecond timestamps; datetime supports microseconds.
        value = re.sub(r"(\.\d{6})\d+", r"\1", value)
        return dt.datetime.fromisoformat(value.replace("Z", "+00:00"))
    except (ValueError, TypeError):
        return None


def docker(*args, timeout=25):
    result = subprocess.run(["sudo", "-n", "docker", *args], capture_output=True, text=True, timeout=timeout)
    if result.returncode:
        # Do not forward arbitrary container errors that might contain credentials.
        raise RuntimeError("Docker command failed: " + args[0])
    return result.stdout


CONTAINER_STATUS = r'''
set -u
end=$(cat /root/rustserver/server/wipeunixtime 2>/dev/null || true)
created=$(stat -c %W /root/rustserver/server/wipeunixtime 2>/dev/null || echo 0)
if [ "$created" -le 0 ]; then created=$(stat -c %Y /root/rustserver/server/wipeunixtime 2>/dev/null || echo 0); fi
history_status=ok
history=null
if [ ! -f /root/rustserver/server/all-playerlist.json ]; then
  history_status=missing
else
  history=$(jq -se '[.[] | if type == "array" then .[] else . end] | map(select(.steamid != null))' /root/rustserver/server/all-playerlist.json 2>/dev/null)
  if [ "$?" -ne 0 ]; then history=null; history_status=invalid; fi
fi
maprow=$(tail -n 1 /root/rustserver/server/map-urls.csv 2>/dev/null || true)
current=null
rcon_status=unavailable
if [ -z "${ENV_RCON_PORT:-}" ] || [ -z "${ENV_RCON_PASSWD:-}" ]; then
  rcon_status=configuration_missing
else
  current=$(timeout -k 1s 8s rcon -t web -T 5s -a "127.0.0.1:${ENV_RCON_PORT}" -p "$ENV_RCON_PASSWD" playerlist 2>&1)
  rcon_exit=$?
  if [ "$rcon_exit" -eq 124 ] || [ "$rcon_exit" -eq 137 ]; then
    rcon_status=timeout; current=null
  elif [ "$rcon_exit" -ne 0 ]; then
    # Classify locally; never send raw connection errors or credentials to the PC.
    case "$current" in
      *"connection reset by peer"*) rcon_status=connection_reset ;;
      *"connection refused"*) rcon_status=unavailable ;;
      *"invalid password"*|*"bad password"*|*"authentication failed"*) rcon_status=authentication_failed ;;
      *) rcon_status=unavailable ;;
    esac
    current=null
  elif printf '%s' "$current" | jq -e 'type == "array"' >/dev/null 2>&1; then
    rcon_status=ok
  else
    rcon_status=invalid_response; current=null
  fi
fi
# A container may wipe while a read is in flight; don't combine two seasons.
end_after=$(cat /root/rustserver/server/wipeunixtime 2>/dev/null || true)
if [ "$end" != "$end_after" ]; then
  end="$end_after"; created=0; history=null; history_status=season_changed; current=null; rcon_status=season_changed; maprow=''
fi
jq -n --arg name "${ENV_SERVERNAME:-}" --arg rcon_port "${ENV_RCON_PORT:-}" --arg max "${ENV_MAXPLAYERS:-}" --arg cycle "${ENV_WIPE_CYCLE:-}" --arg end "$end" --arg created "$created" --arg maprow "$maprow" --arg size "${ENV_WORLDSIZE:-0}" --arg seed "$(cat /root/rustserver/server/seed 2>/dev/null || echo 0)" --arg rcon_status "$rcon_status" --arg history_status "$history_status" --argjson current "$current" --argjson history "$history" '{name:$name,rcon_port:$rcon_port,max:$max,cycle:$cycle,end:$end,created:$created,maprow:$maprow,size:$size,seed:$seed,rcon_status:$rcon_status,history_status:$history_status,current:(if $current == null then null else [$current[] | {SteamId:(.SteamID|tostring),Name:.DisplayName,Address:.Address,ConnectionSeconds:.ConnectedSeconds}] end),history:$history}'
'''


def integer(value):
    try:
        return int(value)
    except (ValueError, TypeError):
        return None


def collect(name, include_logs):
    raw = json.loads(docker("exec", name, "sh", "-lc", CONTAINER_STATUS))
    now = dt.datetime.now(UTC).isoformat().replace("+00:00", "Z")
    warnings = []
    rcon_status = raw.get("rcon_status", "ok")
    history_status = raw.get("history_status", "ok")
    if rcon_status != "ok":
        warnings.append({"timeout": "RCON 応答待ち（タイムアウト）", "unavailable": "RCON 接続待ち（起動中・再起動直後の可能性）",
                         "connection_reset": "RCON 接続がサーバー側から切断されました", "authentication_failed": "RCON 認証を確認してください",
                         "configuration_missing": "RCON 接続設定が未設定", "invalid_response": "RCON 応答形式を確認できません",
                         "season_changed": "ワイプ切り替え中・再取得してください"}.get(rcon_status, "RCON を取得できません"))
    if history_status != "ok":
        warnings.append({"missing": "今季履歴は未作成（定期記録待ち）", "invalid": "今季履歴の読み取り待ち",
                         "season_changed": "今季履歴はワイプ切り替え中"}.get(history_status, "今季履歴を取得できません"))
    end = integer(raw["end"])
    days = {"daily": 1, "weekly": 7, "bi-weekly": 14, "monthly": 35}.get(raw["cycle"].lower())
    start = end - days * 86400 if days and end else None
    created = integer(raw.get("created", "0")) or 0
    if not start:
        warnings.append("シーズン期間の取得待ち")
    # Exclude the outgoing server's samples between scheduled wipe time and actual reset.
    cutoff = max(start, created) if start and start <= created < end else start
    wipe = "scheduled:" + str(end) if end else ""
    known = {}
    for record in raw["history"] or []:
        steamid = str(record.get("steamid", ""))
        if not re.fullmatch(r"\d{17}", steamid):
            continue
        first = record.get("addunixtimestamp")
        first = utc(first) if isinstance(first, (int, float)) else ""
        existing = known.get(steamid)
        if existing is None or (first and (not existing["FirstSeen"] or first < existing["FirstSeen"])):
            known[steamid] = {"SteamId": steamid, "Name": record.get("name") or steamid,
                             "FirstSeen": first, "Online": False, "LastSeen": "", "ObservedAt": now,
                             "WipeId": wipe, "BodyAvailable": False}
    season_unique = len(known) if raw["history"] is not None else None
    for record in raw["current"] or []:
        steamid = record["SteamId"]
        info = known.setdefault(steamid, {"SteamId": steamid, "FirstSeen": now, "WipeId": wipe, "BodyAvailable": False})
        info.update(Name=record["Name"], Online=True, LastSeen=now, ObservedAt=now,
                    Address=record.get('Address') or '', ConnectionSeconds=record.get('ConnectionSeconds'))
    map_url = ""
    map_at = ""
    if raw["maprow"]:
        row = next(csv.reader([raw["maprow"]]))
        if len(row) >= 3:
            map_at = row[0]
            mapped = stamp(map_at)
            # Never use a previous season's map, including while a new map is still generating.
            if mapped and start is not None and start <= mapped.timestamp() < end:
                map_url = row[2]
    rcon_port = integer(raw.get("rcon_port"))
    result = {"Container": name, "Name": raw["name"] or name, "Current": len(raw["current"]) if raw["current"] is not None else None,
              "RconPort": rcon_port if rcon_port is not None and 1 <= rcon_port <= 65535 else None,
              "SeasonUnique": season_unique, "Capacity": integer(raw["max"]), "Cycle": raw["cycle"],
              "SeasonStart": utc(start) if start else "", "SeasonEnd": utc(end) if end else "", "CheckedAt": now,
              "WipeId": wipe, "Size": integer(raw["size"]), "Seed": raw["seed"], "RconStatus": rcon_status, "HistoryStatus": history_status,
              "Players": list(known.values()), "MapUrl": map_url, "MapAt": map_at,
              "Peak": None, "PeakAt": "", "PeakLastAt": "", "LogFirstAt": "", "LogLastAt": "",
              "Samples": 0, "PartialLogs": True, "LogCutoff": utc(cutoff) if cutoff else "", "LogError": "", "Error": ""}
    if include_logs and start:
        try:
            read_peak(name, start, cutoff, end, result)
        except (RuntimeError, OSError, subprocess.SubprocessError):
            result["LogError"] = "ログ取得待ち"
            warnings.append("人数ログの取得に失敗しました")
    result["Error"] = " / ".join(warnings)
    if include_logs:
        # Overview is aggregate-only: never transfer individual identities or map URLs.
        for key in ("Players", "MapUrl", "MapAt"):
            result.pop(key, None)
    return result


def read_peak(name, start, cutoff, end, result):
        peak = None
        peak_at = peak_last = ""
        first = last = ""
        samples = 0
        # Process logs on the host. Only counts and timestamps leave the host.
        proc = subprocess.Popen(["sudo", "-n", "docker", "logs", "--timestamps", name], stdout=subprocess.PIPE,
                                stderr=subprocess.STDOUT, text=True, errors="replace")
        for line in proc.stdout:
            token = line.split(" ", 1)[0]
            logged = stamp(token)
            if not logged:
                continue
            normalized = logged.isoformat().replace("+00:00", "Z")
            if not first:
                first = normalized
            last = normalized
            if not (cutoff <= logged.timestamp() < end):
                continue
            match = re.search(r"\bonline:\s*(\d+)\s*/\s*\d+", line)
            if not match:
                continue
            samples += 1
            count = int(match.group(1))
            if peak is None or count > peak:
                peak, peak_at, peak_last = count, normalized, normalized
            elif count == peak:
                peak_last = normalized
        if proc.wait(timeout=10):
            raise RuntimeError("Docker log read failed")
        result.update(Peak=peak, PeakAt=peak_at, PeakLastAt=peak_last, LogFirstAt=first, LogLastAt=last, Samples=samples,
                      PartialLogs=not first or stamp(first).timestamp() > start + 600)


def read_server(name):
    report = collect(name, False)
    known = {p['SteamId']: p for p in report['Players']}
    inventories = []
    warning, save_at, wipe = report['Error'], '', report['WipeId']
    try:
        start, end = stamp(report['SeasonStart']), stamp(report['SeasonEnd'])
        snapshot = read_saved_players(name, int(start.timestamp()) if start else None, int(end.timestamp()) if end else None)
        save_at = utc(snapshot['SavedAt'])
        wipe = 'save:' + str(snapshot['Created']) + ':' + snapshot['SaveWipeId']
        for steamid, saved in snapshot['Players'].items():
            member = known.setdefault(steamid, {'SteamId': steamid, 'Name': saved['Name'], 'Online': False,
                                               'FirstSeen': save_at, 'LastSeen': '', 'ObservedAt': report['CheckedAt']})
            member['BodyAvailable'] = True
            member['PositionReason'] = saved.get('PositionReason', '')
            if saved.get('Position') is not None:
                member.update(saved['Position'])
                member['PositionAt'] = save_at
            inventories.append({'SteamId': steamid, 'WipeId': wipe, 'CapturedAt': save_at,
                                'Current': False, 'Source': 'save', 'Items': saved['Items']})
    except (ValueError, OSError, subprocess.SubprocessError):
        warning = ' / '.join(filter(None, (warning, '座標・所持品のセーブを読み取れません（保存中・未作成・非対応形式）')))
    for member in known.values():
        member['WipeId'] = wipe
        if report['RconStatus'] != 'ok':
            member['ObservedAt'] = ''
    return {'Server': {'Protocol': 1, 'Name': report['Name'], 'WipeId': wipe, 'Seed': str(report['Seed']),
                       'Size': report['Size'] or 0, 'CapturedAt': report['CheckedAt'],
                       'MapAvailable': bool(report['MapUrl']), 'SaveAt': save_at},
            'Players': list(known.values()), 'Inventories': inventories, 'Warning': warning,
            'IpRoute': read_ip_route(name, docker) if any(p.get('Online') for p in known.values()) else {},
            'PresenceAvailable': report['RconStatus'] == 'ok', 'HistoryAvailable': report['HistoryStatus'] == 'ok'}


def read_map(name, expected_wipe):
    report = collect(name, False)
    wipe = report['WipeId']
    if expected_wipe != wipe:
        start, end = stamp(report['SeasonStart']), stamp(report['SeasonEnd'])
        snapshot = read_saved_players(name, int(start.timestamp()) if start else None, int(end.timestamp()) if end else None)
        wipe = 'save:' + str(snapshot['Created']) + ':' + snapshot['SaveWipeId']
    if not expected_wipe or wipe != expected_wipe:
        raise ValueError('Map season changed')
    url = report['MapUrl']
    parsed = urlparse(url)
    if parsed.scheme != 'https' or not parsed.hostname or parsed.username or parsed.password:
        return {'Error': '今季のマップ画像 URL がまだありません。'}
    response = subprocess.run(['sudo', '-n', 'docker', 'exec', name, 'curl', '--fail', '--silent', '--location',
                               '--max-time', '20', '--max-filesize', '16000000', '--proto', '=https',
                               '--proto-redir', '=https', '--', url], capture_output=True, timeout=25)
    if response.returncode or len(response.stdout) > 16000000 or not response.stdout.startswith((b'\xff\xd8', b'\x89PNG')):
        return {'Error': 'マップ画像を取得できません。次の更新で再試行します。'}
    end_after = docker('exec', name, 'cat', '/root/rustserver/server/wipeunixtime').strip()
    if report['WipeId'] != 'scheduled:' + end_after:
        raise ValueError('Map season changed during download')
    return {'WipeId': wipe, 'Data': base64.b64encode(response.stdout).decode('ascii')}


def main():
    mode = REQUEST.get("mode", "overview")
    if mode not in ("overview", "list", "server", "map", "connections", "presence"):
        raise ValueError("Unsupported mode")
    if mode == 'connections':
        print(json.dumps(read_connections(REQUEST), ensure_ascii=False))
        return
    selected = REQUEST.get("container", "")
    if selected and not CONTAINER.fullmatch(selected):
        raise ValueError("Invalid container")
    if mode in ('server', 'map', 'presence'):
        if not selected:
            raise ValueError('Container required')
        result = read_server(selected) if mode == 'server' else read_presence(selected, docker) if mode == 'presence' else read_map(selected, REQUEST.get('wipe', ''))
        print(json.dumps(result, ensure_ascii=False))
        return
    names = [selected] if selected else [n for n in docker("ps", "--format", "{{.Names}}").splitlines() if CONTAINER.fullmatch(n)]
    servers = []
    for name in sorted(names):
        try:
            if mode == 'list':
                title = docker('exec', name, 'sh', '-lc', 'printf "%s" "${ENV_SERVERNAME:-}"').strip()
                servers.append({'Container': name, 'Name': title or name})
            else:
                servers.append(collect(name, True))
        except Exception as ex:
            reason = "コンテナの応答待ち（タイムアウト）" if isinstance(ex, subprocess.TimeoutExpired) else "コンテナ情報を取得できません（再起動中の可能性）"
            servers.append({"Container": name, "Name": name, "RconStatus": "unavailable", "HistoryStatus": "unavailable", "Error": reason})
    print(json.dumps({"CheckedAt": dt.datetime.now(UTC).isoformat().replace("+00:00", "Z"), "Servers": servers}, ensure_ascii=False))


if __name__ == "__main__":
    try:
        main()
    except Exception as ex:
        print(json.dumps({"Error": type(ex).__name__ + ": collection failed"}))
        sys.exit(1)
