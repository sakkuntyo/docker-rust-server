"""Bounded chat history and an explicit global.say action over container RCON.

Request JSON arrives on SSH stdin. Neither messages nor passwords are shell code.
The say action is never retried: a lost response can follow a successful broadcast.
"""
import datetime as dt
import json
import re
import subprocess
import unicodedata

CHAT_RCON = r'''
if [ -z "${ENV_RCON_PORT:-}" ] || [ -z "${ENV_RCON_PASSWD:-}" ]; then exit 2; fi
exec timeout -k 1s 8s rcon -t web -T 6s -a "127.0.0.1:${ENV_RCON_PORT}" -p "$ENV_RCON_PASSWD" -- "$1"
'''


def chat_message(value):
    if not isinstance(value, str) or any(unicodedata.category(c) in ('Cc', 'Cs', 'Zl', 'Zp') for c in value):
        raise ValueError('改行や制御文字を含めず、1行で入力してください。')
    value = value.strip()
    if not value or len(value.encode('utf-16-le')) // 2 > 256:
        raise ValueError('発言は1～256文字で入力してください。')
    return value


def chat_rcon(container, command):
    if not re.fullmatch(r'rust-[A-Za-z0-9][A-Za-z0-9_.-]*', container):
        raise ValueError('Rust コンテナを選択してください。')
    # Only this fixed shell is evaluated. The complete RCON command is one argument.
    result = subprocess.run(['sudo', '-n', 'docker', 'exec', container, 'sh', '-lc',
                             CHAT_RCON, 'rust-monitor-chat', command],
                            capture_output=True, text=True, encoding='utf-8', timeout=12)
    if result.returncode:
        raise RuntimeError('RCON unavailable')
    return result.stdout


def parse_chat(raw):
    if len(raw) > 2_000_000:
        raise ValueError('Chat response too large')
    rows = json.loads(raw)
    if not isinstance(rows, list) or len(rows) > 200:
        raise ValueError('Invalid chat response')
    result = []
    for row in rows:
        if not isinstance(row, dict) or not all(isinstance(row.get(k), str) for k in ('Message', 'UserId', 'Username')):
            raise ValueError('Invalid chat entry')
        if type(row.get('Time')) is not int or not 0 <= row['Time'] <= 253402300799 or type(row.get('Channel')) is not int:
            raise ValueError('Invalid chat timestamp/channel')
        result.append({'Time': row['Time'], 'Channel': row['Channel'], 'UserId': row['UserId'][:32],
                       'Username': row['Username'][:256], 'Message': row['Message'][:4096]})
    return result


def chat_request(request):
    mode, container = request.get('mode'), request.get('container', '')
    if mode not in ('chat', 'say') or not isinstance(container, str) or not re.fullmatch(r'rust-[A-Za-z0-9][A-Za-z0-9_.-]*', container):
        return {'Error': 'チャットの接続先を確認してください。'}
    if mode == 'chat':
        try:
            rows = parse_chat(chat_rcon(container, 'chat.tail 200'))
            return {'CheckedAt': dt.datetime.now(dt.timezone.utc).isoformat(), 'Messages': rows}
        except (ValueError, RuntimeError, subprocess.TimeoutExpired, OSError):
            return {'Error': 'チャットを取得できません。SSH / RCON の接続状態を確認してください。'}
    try:
        message = chat_message(request.get('message'))
    except ValueError as ex:
        return {'Error': str(ex)}
    try:
        # Rust global.say broadcasts Arg.FullString verbatim. Quoting/escaping the
        # text here would add visible quotes. RCON executes this single command.
        response = chat_rcon(container, 'global.say ' + message)
        if response.strip():
            return {'Accepted': False}
        return {'Accepted': True}
    except (RuntimeError, subprocess.TimeoutExpired, OSError):
        return {'Accepted': False}


if __name__ == '__main__':
    print(json.dumps(chat_request(globals().get('REQUEST', {})), ensure_ascii=False))
