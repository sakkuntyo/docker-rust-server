#!/bin/bash
set -euo pipefail
source_dir=$(cd "$(dirname "$0")" && pwd)
launch="${LAUNCH_SH:-$source_dir/../launch.sh}"
bash -n "$launch"
test_root=$(mktemp -d /tmp/rapid-discord-test.XXXXXXXX)
notify_function=$(awk '/^notify_wipe_map\(\) \{/ {copying=1} copying {print} copying && /^}$/ {exit}' "$launch")
rust_start=$(awk '/^\.\/RustDedicated -batchmode / {copying=1} copying {print} copying && /^  \) &$/ {exit}' "$launch")
[[ -n "$notify_function" && "$rust_start" == *'tee -p >('* ]]
eval "$notify_function"

# No real curl is invoked: all requests are captured by this shell function.
curl() {
  printf '%s\n' called >> "$CASE_DIR/calls"
  printf '%s\n' "$@" > "$CASE_DIR/curl.args"
  command cat > "$CASE_DIR/payload.json"
  sleep "${MOCK_DELAY:-0}"
  if [[ "${MOCK_STATUS:-0}" -ne 0 ]]; then
    printf 'Simulated sensitive error: %s\n' "$ENV_DISCORD_URL" >&2
  fi
  printf '%s\n' done >> "$CASE_DIR/curl.done"
  return "${MOCK_STATUS:-0}"
}

new_case() {
  CASE_DIR="$test_root/$1"
  mkdir -p "$CASE_DIR/server"
  cp "$source_dir/fixtures/RustDedicated.sh" "$CASE_DIR/RustDedicated"
  chmod +x "$CASE_DIR/RustDedicated"
  cd "$CASE_DIR"
  printf '123\n' > server/seed
  printf '2000000000\n' > server/wipeunixtime
  TARGET_STOP_UNIXTIME=2000000000
  ENV_DISCORD_URL='https://discord.com/api/webhooks/123456789012345678/mock_token'
  ENV_NOTIFY_MSG='${ENV_SERVERNAME}がワイプされました。\n${ENV_WIPE_CYCLE} ${ENV_WIPE_DAY_OF_WEEK} ${ENV_WIPE_TIME}\n次回: ${NEXT_WIPE}\n${MAP_IMAGE_URL}'
  ENV_SERVERNAME='日本語 "rapid" & Friends'
  ENV_WIPE_CYCLE=daily
  ENV_WIPE_DAY_OF_WEEK=Friday
  ENV_WIPE_TIME=09:00
  ENV_WIPE_TYPE=FULL
  ENV_WORLDSIZE=2700
  MOCK_DELAY=0
  MOCK_STATUS=0
  export URL_TEST_CASE=normal TZ=Asia/Tokyo
}
poll() {
  for ((attempt=0; attempt<200; attempt++)); do
    if "$@"; then return 0; fi
    sleep 0.02
  done
  printf 'FAIL: poll %s\n' "$*" >&2
  return 1
}
rows_are() {
  [[ -f server/map-urls.csv ]] && [[ $(wc -l < server/map-urls.csv) -eq "$1" ]]
}
start() {
  eval "$rust_start" > observed.log 2>&1
  pipeline_pid=$!
}
finish() {
  wait "$pipeline_pid"
  poll rows_are "$1"
  sleep 0.05
  awk -F, 'NF != 3 {exit 1}' server/map-urls.csv
  grep -qx 'stdout preserved' observed.log
  grep -qx 'stderr preserved' observed.log
  ! grep -q 'INFO:\|WARN:\|example.com' server/map-urls.csv
  ! grep -q 'mock_token' observed.log
}

new_case first
start
finish 3
poll test -f curl.done
[[ $(wc -l < calls) -eq 1 ]]
expected_message=$(printf '%s\n%s\n次回: %s\n%s' "$ENV_SERVERNAMEがワイプされました。" 'daily Friday 09:00' "$(date -d @2000000000 '+%Y-%m-%d %H:%M:%S %Z')" 'https://files.facepunch.com/rust/map-images/bbbb/proceduralmap.2700.0.288_bbbb.jpg')
jq -e --arg message "$expected_message" '.content == $message and .embeds[0].image.url == "https://files.facepunch.com/rust/map-images/bbbb/proceduralmap.2700.0.288_bbbb.jpg" and .allowed_mentions.parse == []' payload.json >/dev/null
grep -qx -- '--max-time' curl.args
grep -qxF "$ENV_DISCORD_URL?wait=true" curl.args
echo 'PASS: new CSV sends first image exactly once with escaped Japanese/newlines and image embed'
start
finish 6
[[ $(wc -l < calls) -eq 1 ]]
echo 'PASS: ordinary restart appends CSV without notifying again'

new_case no-webhook
unset ENV_DISCORD_URL
start
finish 3
[[ ! -e calls ]]
cmp <(./RustDedicated 2>&1) observed.log
echo 'PASS: missing ENV_DISCORD_URL disables notifications and keeps console output unchanged'

new_case existing-empty
: > server/map-urls.csv
start
finish 3
[[ ! -e calls ]]
echo 'PASS: existing empty CSV does not count as newly created'

new_case invalid-url
ENV_DISCORD_URL+="$ENV_DISCORD_URL"
start
finish 3
[[ ! -e calls ]]
grep -q 'ENV_DISCORD_URL が不正' observed.log
echo 'PASS: accidentally duplicated webhook URL rejected without exposing token'

new_case map-only
export URL_TEST_CASE=map-only
start
finish 1
[[ ! -e calls ]]
awk -F, '$2 !~ /aaaa.map$/ || $3 != "" {exit 1}' server/map-urls.csv
echo 'PASS: missing image never sends prematurely; map URL still persisted'

new_case image-only
export URL_TEST_CASE=image-only
start
finish 3
poll test -f curl.done
[[ $(wc -l < calls) -eq 1 ]]
head -n 1 server/map-urls.csv | awk -F, '$2 != "" || $3 !~ /bbbb.jpg$/ {exit 1}'
echo 'PASS: first image still triggers when no map URL precedes it'

new_case interrupted-row
printf '%s' '2026-09-06T00:00:00Z,https://files.facepunch.com/rust/maps/old.map' > server/map-urls.csv
export URL_TEST_CASE=image-only
start
finish 4
[[ ! -e calls ]]
head -n 1 server/map-urls.csv | awk -F, '$2 !~ /old.map$/ || $3 != "" {exit 1}'
echo 'PASS: interrupted old row repaired without false wipe notification'

new_case failed-http
MOCK_STATUS=22
start
finish 3
poll test -f curl.done
poll grep -q 'ワイプ通知に失敗' observed.log
[[ $(wc -l < calls) -eq 1 ]]
echo 'PASS: HTTP failure is logged once, no secret leak, no duplicate retry, no CSV corruption'

new_case slow-http
MOCK_DELAY=2
start
poll rows_are 3
[[ -f calls && ! -f curl.done ]]
finish 3
poll test -f curl.done
echo 'PASS: stalled notification does not block later map/image CSV records or console output'

new_case safe-template
ENV_SERVERNAME='Name ${ENV_WIPE_TIME} & "quote" $(touch should-not-exist-1)'
ENV_NOTIFY_MSG='${ENV_SERVERNAME}\n$(touch should-not-exist-2) `touch should-not-exist-3` ${ENV_DISCORD_URL} ${UNKNOWN} ${ENV_WIPE_TYPE} ${ENV_WORLDSIZE}'
start
finish 3
poll test -f curl.done
jq -e --arg name "$ENV_SERVERNAME" '.content | startswith($name + "\n") and contains("${ENV_DISCORD_URL}") and contains("${UNKNOWN} FULL 2700")' payload.json >/dev/null
[[ ! -e should-not-exist-1 && ! -e should-not-exist-2 && ! -e should-not-exist-3 ]]
! grep -q 'mock_token' payload.json
echo 'PASS: placeholders expand only once; commands are literal and secrets are not interpolated'

new_case default-message
unset ENV_NOTIFY_MSG
start
finish 3
poll test -f curl.done
jq -e '.content | contains("がワイプされました。\nワイプ周期: daily\n次回ワイプ:")' payload.json >/dev/null
echo 'PASS: omitted message uses a default including the image URL and next wipe time'

new_case real-newline
ENV_NOTIFY_MSG=$'一行目\n${ENV_SERVERNAME}\n${MAP_IMAGE_URL}'
start
finish 3
poll test -f curl.done
jq -e '.content | startswith("一行目\n日本語")' payload.json >/dev/null
echo 'PASS: literal multiline environment values work too'

new_case too-long
ENV_NOTIFY_MSG=$(printf '%2001s' x)
start
finish 3
[[ ! -e calls ]]
grep -q '2000文字以内' observed.log
echo 'PASS: over-limit message does not send or damage CSV'

if [[ -n "${BASELINE_LAUNCH_SH:-}" ]]; then
  cmp <(sed -n '/^# 10分後に死活監視を開始/,$p' "$launch") <(sed -n '/^# 10分後に死活監視を開始/,$p' "$BASELINE_LAUNCH_SH")
  cmp <(sed '/^# ENV_NOTIFY_MSG の /,$d' "$launch") <(sed '/^\.\/RustDedicated -batchmode /,$d' "$BASELINE_LAUNCH_SH")
  echo 'PASS: SteamCMD, wipe logic, uMod, map copies, owner permissions and health loop unchanged'
fi
printf 'Disposable test fixture: %s\n' "$test_root"
