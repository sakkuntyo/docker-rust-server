#!/bin/bash

# docker stop 時の保存処理
trap '
rcon -t web -a 127.0.0.1:${ENV_RCON_PORT:=28016} -p "${ENV_RCON_PASSWD:=StrongPasswd123456}" "global.say サーバーを停止中...../Shutting down server.....";
rcon -t web -a 127.0.0.1:${ENV_RCON_PORT:=28016} -p "${ENV_RCON_PASSWD:=StrongPasswd123456}" "save";
rcon -t web -a 127.0.0.1:${ENV_RCON_PORT:=28016} -p "${ENV_RCON_PASSWD:=StrongPasswd123456}" "quit";
sleep 10
exit 0;
' SIGTERM

# ワイプ周期が来ている場合は ./server/seed ファイルを消してリセットする
if [ -f "./server/wipeunixtime" ]; then 
  echo "INFO: --------------------ワイプ周期チェック"
  echo "INFO: 現在の時刻　　 -> $(date "+%Y/%m/%d %T")"
  echo "INFO: ワイプ予定時刻 -> $(date -d "@$(cat ./server/wipeunixtime)" "+%Y/%m/%d %T")"
  echo "INFO: ワイプの種類   -> ${ENV_WIPE_TYPE:=FULL}"
  echo "INFO: --------------------"
  if [[ "$(date +%s)" -gt "$(cat ./server/wipeunixtime)" ]]; then
    echo "INFO: ワイプを行います。"
    rm ./server/seed
    rm ./server/wipeunixtime
    if [ "${ENV_WIPE_TYPE:=FULL}" == "FULL" ]; then
      rm -r ./server/*
    fi
    echo "INFO: ワイプ処理を完了しました"
  fi
fi

# WIPE TIME から停止時間を決定
if [[ "$(date +%s)" -lt $(date -d "${ENV_WIPE_TIME:=09:00}" +%s) ]];then 
  TARGET_STOP_UNIXTIME=$(date -d "${ENV_WIPE_TIME:=09:00}" +%s);
else
  TARGET_STOP_UNIXTIME=$(date -d "tomorrow ${ENV_WIPE_TIME:=09:00}" +%s);
fi
echo "INFO: --------------------ワイプと停止周期"
echo "INFO: 定期停止時刻: ${ENV_WIPE_TIME:=09:00}"
echo "INFO: 次の停止時刻: $(date -d "@${TARGET_STOP_UNIXTIME}" '+%Y/%m/%d %T')"

# 初回起動時に現在時刻(unixtime)のseed値と作成日時
mkdir -p ./server
if [ ! -f "./server/seed" ] && [ ! -z "${ENV_SEED}" ] ; then echo "${ENV_SEED}" > ./server/seed; fi
if [ ! -f "./server/seed" ]; then date +%s > ./server/seed; fi
if [ ! -f "./server/wipeunixtime" ]; then
  # ENV_WIPE_CYCLE を date -d に指定する文字列へ変換した変数を作成
  case ${ENV_WIPE_CYCLE:=monthly} in
  "monthly")
    ENV_WIPE_CYCLE_DATED="5 week"
    ;;
  "bi-weekly")
    ENV_WIPE_CYCLE_DATED="2 week"
    ;;
  "weekly")
    ENV_WIPE_CYCLE_DATED="1 week"
    ;;
  "daily")
    ENV_WIPE_CYCLE_DATED="1 days"
    ;;
  esac
  
  echo "INFO: ワイプ周期: ${ENV_WIPE_CYCLE}"
  echo "INFO: ワイプ曜日: ${ENV_WIPE_DAY_OF_WEEK:=Friday}"
  date -d "$(echo "${ENV_WIPE_DAY_OF_WEEK:=Friday} ${ENV_WIPE_CYCLE_DATED} ${ENV_WIPE_TIME:=09:00}" | sed "s/.* 1 days/1 days/g")" +%s > ./server/wipeunixtime;
  echo "INFO: ワイプ予定時刻: $(date -d "@$(cat ./server/wipeunixtime)" "+%Y/%m/%d %T")"
fi
echo "INFO: --------------------"

# update rustdedicated
steamcmd +login anonymous +force_install_dir /root/rustserver +app_update 258550 validate +quit

install_umod_and_plugins() {
  local umod_zip="/tmp/Oxide.Rust.zip"
  local umod_url="${ENV_UMOD_DOWNLOAD_URL:=https://github.com/OxideMod/Oxide.Rust/releases/latest/download/Oxide.Rust-linux.zip}"
  local oxide_config="/root/rustserver/oxide/oxide.config.json"
  local umod_modded="${ENV_UMOD_MODDED:=false}"

  if [[ "${umod_modded}" != "true" ]]; then
    umod_modded="false"
  fi

  echo "INFO: Installing uMod/Oxide from ${umod_url}"
  if ! curl -fsSL --retry 3 --retry-delay 5 "${umod_url}" -o "${umod_zip}"; then
    echo "ERROR: Failed to download uMod/Oxide."
    exit 1
  fi

  if ! unzip -oq "${umod_zip}" -d /root/rustserver; then
    echo "ERROR: Failed to extract uMod/Oxide."
    exit 1
  fi
  rm -f "${umod_zip}"

  mkdir -p /root/rustserver/oxide/plugins
  if [[ -f "${oxide_config}" ]]; then
    jq --argjson modded "${umod_modded}" '.Options.Modded = $modded' "${oxide_config}" > "${oxide_config}.tmp" && mv "${oxide_config}.tmp" "${oxide_config}"
  else
    cat > "${oxide_config}" <<EOF
{
  "Options": {
    "Modded": ${umod_modded},
    "PluginWatchers": true,
    "DefaultGroups": {
      "Players": "default",
      "Administrators": "admin"
    },
    "WebRequestIP": "0.0.0.0"
  },
  "Commands": {
    "Chat command prefixes": [
      "/"
    ]
  },
  "Plugin Compiler": {
    "Shutdown on idle": true,
    "Seconds before idle": 60,
    "Preprocessor directives": [],
    "Enable Publicizer": true,
    "Ignored Publicizer References": []
  },
  "OxideConsole": {
    "Enabled": true,
    "MinimalistMode": true,
    "ShowStatusBar": true
  },
  "OxideRcon": {
    "Enabled": false,
    "Port": 25580,
    "Password": "",
    "ChatPrefix": "[Server Console]"
  }
}
EOF
  fi
  echo "INFO: uMod/Oxide Modded flag is ${umod_modded}."

  install_optional_plugin() {
    local enabled="$1"
    local plugin_file="$2"
    local plugin_url="$3"
    local plugin_name="$4"

    if [[ "${enabled}" != "true" ]]; then
      return
    fi

    echo "INFO: Installing ${plugin_name} plugin."
    if [[ -f "/opt/rustserver/plugins/${plugin_file}" ]]; then
      cp -f "/opt/rustserver/plugins/${plugin_file}" "/root/rustserver/oxide/plugins/${plugin_file}"
    elif ! curl -fsSL --retry 3 --retry-delay 5 "${plugin_url}" -o "/root/rustserver/oxide/plugins/${plugin_file}"; then
      echo "ERROR: Failed to install ${plugin_name} plugin."
      exit 1
    fi
  }

  install_optional_plugin "${ENV_ENABLE_ADMIN_RADAR:=true}" "AdminRadar.cs" "https://umod.org/plugins/AdminRadar.cs" "AdminRadar"
  install_optional_plugin "${ENV_ENABLE_INVENTORY_VIEWER:=true}" "InventoryViewer.cs" "https://umod.org/plugins/InventoryViewer.cs" "Inventory Viewer"
  install_optional_plugin "${ENV_ENABLE_PLAYER_ADMINISTRATION:=true}" "PlayerAdministration.cs" "https://umod.org/plugins/PlayerAdministration.cs" "Player Administration"
  install_optional_plugin "${ENV_ENABLE_ADMIN_LOGGER:=true}" "AdminLogger.cs" "https://umod.org/plugins/AdminLogger.cs" "Admin Logger"
  install_optional_plugin "${ENV_ENABLE_VANISH:=true}" "Vanish.cs" "https://umod.org/plugins/Vanish.cs" "Vanish"
}

if [[ "${ENV_ENABLE_UMOD:=true}" == "true" ]]; then
  install_umod_and_plugins
fi

OWNER_PERMISSION_STATUS_FILE="/root/rustserver/server/umod-owner-permissions.status"
OWNER_PERMISSION_STATUS_SCHEMA_VERSION="1"

normalized_ownerids() {
  printf "%s" "${ENV_OWNERIDS:-}" | tr -d '"' | tr ',' ' ' | xargs
}

owner_permission_key() {
  echo "schema_version=${OWNER_PERMISSION_STATUS_SCHEMA_VERSION}"
  echo "ownerids=$(normalized_ownerids)"
  echo "enable_umod=${ENV_ENABLE_UMOD:=true}"
  echo "enable_admin_radar=${ENV_ENABLE_ADMIN_RADAR:=true}"
  echo "enable_inventory_viewer=${ENV_ENABLE_INVENTORY_VIEWER:=true}"
  echo "enable_player_administration=${ENV_ENABLE_PLAYER_ADMINISTRATION:=true}"
  echo "enable_admin_logger=${ENV_ENABLE_ADMIN_LOGGER:=true}"
  echo "enable_vanish=${ENV_ENABLE_VANISH:=true}"
}

owner_permissions_list() {
  if [[ "${ENV_ENABLE_ADMIN_RADAR:=true}" == "true" ]]; then
    echo "adminradar.allowed"
    echo "adminradar.auto"
  fi

  if [[ "${ENV_ENABLE_INVENTORY_VIEWER:=true}" == "true" ]]; then
    echo "inventoryviewer.allowed"
    echo "inventoryviewer.unlock"
  fi

  if [[ "${ENV_ENABLE_VANISH:=true}" == "true" ]]; then
    echo "vanish.allow"
    echo "vanish.unlock"
    echo "vanish.invviewer"
    echo "vanish.teleport"
  fi

  if [[ "${ENV_ENABLE_PLAYER_ADMINISTRATION:=true}" == "true" ]]; then
    for PERMISSION in \
      playeradministration.access.show \
      playeradministration.access.kick \
      playeradministration.access.ban \
      playeradministration.access.kill \
      playeradministration.access.clearinventory \
      playeradministration.access.resetblueprint \
      playeradministration.access.resetmetabolism \
      playeradministration.access.recovermetabolism \
      playeradministration.access.hurt \
      playeradministration.access.heal \
      playeradministration.access.mute \
      playeradministration.access.perms \
      playeradministration.access.allowfreeze \
      playeradministration.access.teleport \
      playeradministration.access.spectate \
      playeradministration.access.detailedinfo \
      playeradministration.protect.ban \
      playeradministration.protect.hurt \
      playeradministration.protect.kick \
      playeradministration.protect.kill \
      playeradministration.protect.reset
    do
      echo "${PERMISSION}"
    done
  fi
}

owner_permission_status_matches() {
  if [[ ! -f "${OWNER_PERMISSION_STATUS_FILE}" ]]; then
    return 1
  fi
  grep -qxF "status=applied" "${OWNER_PERMISSION_STATUS_FILE}" || return 1
  while IFS= read -r EXPECTED_LINE; do
    grep -qxF "${EXPECTED_LINE}" "${OWNER_PERMISSION_STATUS_FILE}" || return 1
  done < <(owner_permission_key)
  cmp -s <(awk '
    /^permissions_begin$/ { in_permissions = 1; next }
    /^permissions_end$/ { in_permissions = 0 }
    in_permissions { print }
  ' "${OWNER_PERMISSION_STATUS_FILE}") <(owner_permissions_list) || return 1
}

write_owner_permission_status() {
  local STATUS="$1"
  local DETAIL="$2"
  mkdir -p "$(dirname "${OWNER_PERMISSION_STATUS_FILE}")"
  {
    echo "status=${STATUS}"
    echo "updated_at=$(date -Iseconds)"
    owner_permission_key
    echo "detail=${DETAIL}"
    echo "permissions_begin"
    owner_permissions_list
    echo "permissions_end"
  } > "${OWNER_PERMISSION_STATUS_FILE}.tmp"
  mv "${OWNER_PERMISSION_STATUS_FILE}.tmp" "${OWNER_PERMISSION_STATUS_FILE}"
}

grant_owner_permissions() {
  local OWNER_COUNT=0
  local OWNERS
  local RCON_COMMANDS="/tmp/umod-owner-permissions.commands"
  local RCON_CHUNK="/tmp/umod-owner-permissions.chunk"
  local RCON_OUTPUT="/tmp/umod-owner-permissions.output"
  local CHUNK_SIZE=2
  local CHUNK_COUNT=0
  local FAILED=0

  OWNERS="$(normalized_ownerids)"
  : > "${RCON_COMMANDS}"
  for OWNERID in ${OWNERS}; do
    if [[ -z "${OWNERID}" ]]; then
      continue
    fi

    OWNER_COUNT=$((OWNER_COUNT + 1))
    echo "global.ownerid ${OWNERID}" >> "${RCON_COMMANDS}"
    while IFS= read -r PERMISSION; do
      if [[ -z "${PERMISSION}" ]]; then
        continue
      fi
      echo "oxide.grant user ${OWNERID} ${PERMISSION}" >> "${RCON_COMMANDS}"
    done < <(owner_permissions_list)
  done

  if [[ "${OWNER_COUNT}" -eq 0 ]]; then
    echo "WARN: ENV_OWNERIDS is empty. No owner permissions were granted."
    rm -f "${RCON_COMMANDS}"
    return 0
  fi

  : > "${RCON_CHUNK}"
  while IFS= read -r RCON_COMMAND; do
    echo "${RCON_COMMAND}" >> "${RCON_CHUNK}"
    CHUNK_COUNT=$((CHUNK_COUNT + 1))
    if [[ "${CHUNK_COUNT}" -ge "${CHUNK_SIZE}" ]]; then
      rcon -t web -T 30s -a 127.0.0.1:${ENV_RCON_PORT:=28016} -p "${ENV_RCON_PASSWD:=StrongPasswd123456}" < "${RCON_CHUNK}" > "${RCON_OUTPUT}" 2>&1 || true
      if grep -Eqi "connection refused|i/o timeout|invalid value|invalid password|bad password|no such host|Permission .*does not exist|Permission .*doesn't exist|Unknown command|Incorrect Usage" "${RCON_OUTPUT}"; then
        FAILED=1
      fi
      : > "${RCON_CHUNK}"
      CHUNK_COUNT=0
      sleep 1
    fi
  done < "${RCON_COMMANDS}"

  if [[ "${CHUNK_COUNT}" -gt 0 ]]; then
    rcon -t web -T 30s -a 127.0.0.1:${ENV_RCON_PORT:=28016} -p "${ENV_RCON_PASSWD:=StrongPasswd123456}" < "${RCON_CHUNK}" > "${RCON_OUTPUT}" 2>&1 || true
    if grep -Eqi "connection refused|i/o timeout|invalid value|invalid password|bad password|no such host|Permission .*does not exist|Permission .*doesn't exist|Unknown command|Incorrect Usage" "${RCON_OUTPUT}"; then
      FAILED=1
    fi
  fi

  rm -f "${RCON_COMMANDS}" "${RCON_CHUNK}" "${RCON_OUTPUT}"
  return "${FAILED}"
}

ensure_owner_permissions_applied() {
  if [[ "${ENV_ENABLE_UMOD:=true}" != "true" ]]; then
    write_owner_permission_status "skipped" "uMod is disabled"
    return 0
  fi

  if owner_permission_status_matches; then
    return 0
  fi

  echo "INFO: Applying owner permissions for uMod admin tools."
  if grant_owner_permissions; then
    write_owner_permission_status "applied" "owner permissions applied successfully"
    echo "INFO: Owner permissions status written to ${OWNER_PERMISSION_STATUS_FILE}."
    return 0
  fi

  write_owner_permission_status "failed" "owner permission application failed; will retry on next health check"
  echo "WARN: Failed to apply all owner permissions. Will retry on next health check."
  return 1
}

# exitnode 指定があるなら tailscale を起動 (特権モードが必要)
if [ ! -z "${ENV_TS_EXITNODE_IP}" ]; then
  # デバッグログを出させるが、docker logs -t で時刻を表示できるので時刻部分は sed で削除
  tailscaled -verbose 1 | \
    sed -u 's/^[0-9]\{4\}\/[0-9]\{2\}\/[0-9]\{2\} [0-9]\{2\}:[0-9]\{2\}:[0-9]\{2\} //g' &
  # そろそろここも if then 形式に変えたいけど、ちゃんと動いてる。。。。
  tailscale status && {
    tailscale up --exit-node="${ENV_TS_EXITNODE_IP}" --hostname=${ENV_TS_HOSTNAME}
    :
  } || {
    tailscale up --auth-key=${ENV_TS_AUTHKEY} --exit-node="${ENV_TS_EXITNODE_IP}" --hostname=${ENV_TS_HOSTNAME}
    :
  }
fi

./RustDedicated -batchmode \
        +server.identity "serverdata1" \
        +server.hostname "${ENV_SERVERNAME:=TEST SERVER}" \
        +server.description "Next wipe:$(date -d "@$(cat ./server/wipeunixtime)" '+%Y-%m-%d_%T(%Z)')\n---\n${ENV_SERVERDESCRIPTION:=Welcome!}\n---\nMax team size:${ENV_MAXTEAMSIZE:=8}\nMax players:${ENV_MAXPLAYERS:=100}\nWorld size:${ENV_WORLDSIZE:=3000}\nWipe schedule:${ENV_WIPE_CYCLE:=Monthly}\nWipe type: ${ENV_WIPE_TYPE:=FULL}\nNext restart/stop time:$(date -d "@${TARGET_STOP_UNIXTIME}" '+%Y-%m-%d_%T(%Z)')\nLive Streaming:${ENV_LIVE_STREAM_POLICY:=OK}" \
        +server.logoimage "${ENV_SERVERLOGOIMG:=https://github.com/user-attachments/assets/9cb873a1-b0c8-4d01-9dfc-df41bb2468e5}" \
        +server.url "${ENV_SERVERURL:=https://github.com/sakkuntyo/docker-rust-server}" \
        +server.seed "$(cat ./server/seed)" \
        +server.worldsize ${ENV_WORLDSIZE:=3000} \
        +server.maxplayers ${ENV_MAXPLAYERS:=100} \
        +server.maxconnectionsperip 500 \
        +app.maxconnectionsperip 500 \
        +relationshipmanager.maxteamsize ${ENV_MAXTEAMSIZE:=8} \
        +sv.secure 1 \
        +sv.EAC 1 \
        +rcon.password "${ENV_RCON_PASSWD:=StrongPasswd123456}" \
        +server.port ${ENV_SERVER_PORT:=28015} \
        +rcon.port ${ENV_RCON_PORT:=28016} \
        +server.queryport ${ENV_QUERY_PORT:=28017} \
        +server.tags "${ENV_SERVERTAGS:=Vanilla}" &

# 10分後に死活監視を開始
for ((i = 1; i <= 20; i++))
do
  echo "INFO: $(((21 - i))) 分後にヘルスチェックを開始します。。。"
  sleep 60
  ensure_owner_permissions_applied
done

while true; do
  TIMESTAMP=$(date)

  # Tailscaleのチェックが必要かどうかを判断するフラグ
  # ENV_TS_EXITNODE_IP が空でなければ (設定されていれば)、true に設定
  SHOULD_CHECK_TAILSCALED=false
  if [[ -n "${ENV_TS_EXITNODE_IP}" ]]; then
    SHOULD_CHECK_TAILSCALED=true
  fi

  # --- ヘルスチェックの実施 ---
  # 1. tailscaled のチェックが必要であり、かつ tailscaled が起動していない場合
  if [[ "${SHOULD_CHECK_TAILSCALED}" == "true" && -z "$(pgrep tailscaled)" ]]; then
    echo "ERROR: tailscaled が起動していません。コンテナを停止します (必要に応じて自動起動オプションを使用してください)。"
    kill 1
  # 2. RustDedicated プロセスが存在しない場合 (tailscaled のチェックがOKか、スキップされた場合)
  elif ! pgrep RustDedicated > /dev/null; then
    echo "ERROR: RustDedicated が起動していません。コンテナを停止します (必要に応じて自動起動オプションを使用してください)。"
    kill 1
  # 3. ポート28015がリッスンされていない場合 (両プロセスがOKの場合)
  elif ! netstat -tuln | grep "${ENV_SERVER_PORT:=28015}" > /dev/null; then
    echo "ERROR: ポート ${ENV_SERVER_PORT:=28015} のリッスンがありません。コンテナを停止します (必要に応じて自動起動オプションを使用してください)。"
    kill 1
  # 4. 全てのチェックがOKの場合
  else
    echo "INFO: Health Check: 全てのサービスは正常に稼働中です。"

    # サーバーバージョンアップデート対策
    if [ ! -f "./server/createdServerVersion" ]; then 
      echo "INFO: サーバーデータのシンボリックリンクを作成します。これは初回起動時にのみ行います。"
      (
        cd server/serverdata1/
        createdServerVersion=$(find . -maxdepth 1 -name "proceduralmap.${ENV_WORLDSIZE:=3000}.$(cat ../seed).*.sav" | sed -r 's/.*\.([0-9]{3,4})\.sav/\1/g' | sort -u -n | head -n1)
        createdBpVersion=$(find . -maxdepth 1 -name "player.blueprints.*.db" | sed -r 's/.*player\.blueprints\.([0-9]+)\.db/\1/g' | sort -u -n | head -n1)
        createdIdentityVersion=$(find . -maxdepth 1 -name "player.identities.*.db" | sed -r 's/.*player\.identities\.([0-9]+)\.db/\1/g' | sort -u -n | head -n1)
        createdDeathVersion=$(find . -maxdepth 1 -name "player.deaths.*.db" | sed -r 's/.*player\.deaths\.([0-9]+)\.db/\1/g' | sort -u -n | head -n1)
        createdRelationshipVersion=$(find . -maxdepth 1 -name "relationship.*.db" | sed -r 's/.*relationship\.([0-9]+)\.db/\1/g' | sort -u -n | head -n1)
        for i in {1..10};do
          ln -sf "proceduralmap.${ENV_WORLDSIZE:=3000}.$(cat ../seed).${createdServerVersion}.sav" "proceduralmap.${ENV_WORLDSIZE:=3000}.$(cat ../seed).$(((${createdServerVersion} + $i))).sav";
          ln -sf "player.states.${createdServerVersion}.db" "player.states.$(((${createdServerVersion} + $i))).db";
          ln -sf "player.states.${createdServerVersion}.db-wal" "player.states.$(((${createdServerVersion} + $i))).db-wal";
          ln -sf "sv.files.${createdServerVersion}.db" "sv.files.$(((${createdServerVersion} + $i))).db";
          ln -sf "sv.files.${createdServerVersion}.db-wal" "sv.files.$(((${createdServerVersion} + $i))).db-wal";
          if [ ! -z "${createdBpVersion}" ]; then
            ln -sf "player.blueprints.${createdBpVersion}.db" "player.blueprints.$(((${createdBpVersion} + $i))).db";
            ln -sf "player.blueprints.${createdBpVersion}.db-wal" "player.blueprints.$(((${createdBpVersion} + $i))).db-wal";
          fi
          if [ ! -z "${createdIdentityVersion}" ]; then
            ln -sf "player.identities.${createdIdentityVersion}.db" "player.identities.$(((${createdIdentityVersion} + $i))).db";
            ln -sf "player.identities.${createdIdentityVersion}.db-wal" "player.identities.$(((${createdIdentityVersion} + $i))).db-wal";
          fi
          if [ ! -z "${createdDeathVersion}" ]; then
            ln -sf "player.deaths.${createdDeathVersion}.db" "player.deaths.$(((${createdDeathVersion} + $i))).db";
            ln -sf "player.deaths.${createdDeathVersion}.db-wal" "player.deaths.$(((${createdDeathVersion} + $i))).db-wal";
          fi
          if [ ! -z "${createdRelationshipVersion}" ]; then
            ln -sf "relationship.${createdRelationshipVersion}.db" "relationship.$(((${createdRelationshipVersion} + $i))).db";
            ln -sf "relationship.${createdRelationshipVersion}.db-wal" "relationship.$(((${createdRelationshipVersion} + $i))).db-wal";
          fi
        done
        echo "INFO: サーバーデータのシンボリックリンクを作成しました。"
        echo "${createdServerVersion}" > ../createdServerVersion
      )
      echo "INFO: createdServerVersion -> $(cat ./server/createdServerVersion)"
    fi

    # pop 定期
    if [[ $(date "+%M") -eq "30" || $(date "+%M") -eq "0" ]];then
      onlinecount=$(rcon -t web -a 127.0.0.1:${ENV_RCON_PORT:=28016} -p "${ENV_RCON_PASSWD:=StrongPasswd123456}" "playerlist" | jq -c "[ .[] ] | length")
      rcon -t web -a 127.0.0.1:${ENV_RCON_PORT:=28016} -p "${ENV_RCON_PASSWD:=StrongPasswd123456}" "playerlist" | jq -c ".[] | {steamid: .SteamID, name: .DisplayName, addunixtimestamp: "$(date +%s)"}" > /tmp/new-playerlist.json
      touch ./server/all-playerlist.json
      cat ./server/all-playerlist.json /tmp/new-playerlist.json | jq -s "group_by(.steamid)[] | min_by(.addunixtimestamp)" > /tmp/all-playerlist.json
      cp /tmp/all-playerlist.json ./server/all-playerlist.json
      allcount=$(cat ./server/all-playerlist.json | jq -s "[ .[] ] | length")
      rcon -t web -a 127.0.0.1:${ENV_RCON_PORT:=28016} -p "${ENV_RCON_PASSWD:=StrongPasswd123456}" "global.say online: ${onlinecount} / ${ENV_MAXPLAYERS:=100} | sleeping: $(( ${allcount} - ${onlinecount} ))"
    fi

    # admin 自動追加
    ensure_owner_permissions_applied
  fi  
  
  echo "DEBUG: --------------------"
  echo "DEBUG: 現在時刻: $(date '+%Y/%m/%d %T')"
  echo "DEBUG: 停止時刻: $(date -d @${TARGET_STOP_UNIXTIME} '+%Y/%m/%d %T')"
  echo "DEBUG: 現在時刻 > 停止時刻 = $(if [[ $(date '+%s') -gt "$(date -d @${TARGET_STOP_UNIXTIME} '+%s')" ]] ; then echo true; else echo false; fi)"
  echo "DEBUG: --------------------"

  # 停止する時刻を過ぎたなら停止
  if [[ "$(date +%s)" -gt "${TARGET_STOP_UNIXTIME}" ]]; then
    echo "INFO: 停止時刻となったため停止します。"
    kill 1
  # 1 時間前ならアナウンス
  elif [ -z ${REBOOTMSG_1HOUR_SENT_FLG} ] && [[ "$(date +%s)" -gt "$(date -d "$(date -d @${TARGET_STOP_UNIXTIME}) -1 hour" +%s)" ]]; then
    echo "INFO: 再起動/停止の1時間前になりました。"
    rcon -t web -a 127.0.0.1:${ENV_RCON_PORT:=28016} -p "${ENV_RCON_PASSWD:=StrongPasswd123456}" "global.say サーバーは1時間後に停止/再起動されます。/ Server will restart or stop in an hour.";
    REBOOTMSG_1HOUR_SENT_FLG=true
  # 30分前ならアナウンス
  elif [ -z ${REBOOTMSG_30MIN_SENT_FLG} ] && [[ "$(date +%s)" -gt "$(date -d "$(date -d @${TARGET_STOP_UNIXTIME}) -30 minutes" +%s)" ]]; then
    echo "INFO: 再起動/停止の30分前になりました。"
    rcon -t web -a 127.0.0.1:${ENV_RCON_PORT:=28016} -p "${ENV_RCON_PASSWD:=StrongPasswd123456}" "global.say サーバーは30分後に停止/再起動されます。/ Server will restart or stop in 30 minutes.";
    REBOOTMSG_30MIN_SENT_FLG=true
  # 15分前ならアナウンス
  elif [ -z ${REBOOTMSG_15MIN_SENT_FLG} ] && [[ "$(date +%s)" -gt "$(date -d "$(date -d @${TARGET_STOP_UNIXTIME}) -15 minutes" +%s)" ]]; then
    echo "INFO: 再起動/停止の15分前になりました。"
    rcon -t web -a 127.0.0.1:${ENV_RCON_PORT:=28016} -p "${ENV_RCON_PASSWD:=StrongPasswd123456}" "global.say サーバーは15分後に停止/再起動されます。/ Server will restart or stop in 15 minutes.";
    REBOOTMSG_15MIN_SENT_FLG=true
  # 5分前ならアナウンス
  elif [ -z ${REBOOTMSG_5MIN_SENT_FLG} ] && [[ "$(date +%s)" -gt "$(date -d "$(date -d @${TARGET_STOP_UNIXTIME}) -5 minutes" +%s)" ]]; then
    echo "INFO: 再起動/停止の5分前になりました。"
    rcon -t web -a 127.0.0.1:${ENV_RCON_PORT:=28016} -p "${ENV_RCON_PASSWD:=StrongPasswd123456}" "global.say サーバーは5分後に停止/再起動されます。/ Server will restart or stop in 5 minutes.";
    REBOOTMSG_5MIN_SENT_FLG=true
  fi

  sleep 60
done
