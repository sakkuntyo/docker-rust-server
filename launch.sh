#!/bin/bash

# docker stop 時の保存処理
trap '
rcon -t web -a 127.0.0.1:${ENV_RCON_PORT:=28016} -p "${ENV_RCON_PASSWD:=StrongPasswd123456}" "save";
rcon -t web -a 127.0.0.1:${ENV_RCON_PORT:=28016} -p "${ENV_RCON_PASSWD:=StrongPasswd123456}" "quit";
' SIGTERM

# ワイプ周期が来ている場合は ./seed ファイルを消してリセットする
if [ -f "./wipeunixtime" ]; then 
  echo "INFO: 現在の時刻　　 -> $(date "+%Y/%m/%d %T")"
  echo "INFO: ワイプ予定時刻 -> $(date -d "@$(cat ./wipeunixtime)" "+%Y/%m/%d %T")"
  if [[ "$(date +%s)" -gt "$(cat ./wipeunixtime)" ]]; then
    echo "INFO: ワイプを行います。"
    rm ./seed
    rm ./wipeunixtime
    echo "INFO: ワイプ処理を完了しました"
  fi
fi

# STOP TIME が設定されている場合だけ停止時間を決定
if [ ! -z "${ENV_STOP_TIME}" ];then
  if [[ "$(date +%s)" -lt $(date -d "${ENV_STOP_TIME}" +%s) ]];then 
    TARGET_STOP_UNIXTIME=$(date -d "${ENV_STOP_TIME}" +%s);
  else
    TARGET_STOP_UNIXTIME=$(date -d "tomorrow ${ENV_STOP_TIME}" +%s);
  fi
  echo "INFO: 定期停止時刻: ${ENV_STOP_TIME}"
  echo "INFO: 次の停止時刻: $(date -d "@${TARGET_STOP_UNIXTIME}" '+%Y/%m/%d %T')"
fi

# 初回起動時に現在時刻(unixtime)のseed値と作成日時
if [ ! -f "./seed" ] && [ ! -z "${ENV_SEED}" ] ; then echo "${ENV_SEED}" > ./seed; fi
if [ ! -f "./seed" ]; then date +%s > ./seed; fi
if [ ! -f "./wipeunixtime" ]; then
  if [ "${ENV_WIPE_CYCLE}" == "daily" ]; then 
    echo "INFO: ENV_WIPE_CYCLE:daily"
    echo "INFO: ワイプ周期を1日に設定します。"
    date -d "+1 day -30 min" +%s > ./wipeunixtime;
    echo "INFO: ワイプ予定時刻 -> $(date -d "@$(cat ./wipeunixtime)" "+%Y/%m/%d %T")"
  fi
  if [ "${ENV_WIPE_CYCLE}" == "weekly" ]; then 
    echo "INFO: ENV_WIPE_CYCLE:weekly"
    echo "INFO: ワイプ周期を1週間に設定します。"
    date -d "+1 week -30 min" +%s > ./wipeunixtime;
    echo "INFO: 次のワイプ予定時刻 -> $(date -d "@$(cat ./wipeunixtime)" "+%Y/%m/%d %T")"
  fi
  if [ "${ENV_WIPE_CYCLE}" == "bi-weekly" ]; then 
    echo "INFO: ENV_WIPE_CYCLE:bi-weekly"
    echo "INFO: ワイプ周期を2週間に設定します。"
    date -d "+2 week -30 min" +%s > ./wipeunixtime;
    echo "INFO: 次のワイプ予定時刻 -> $(date -d "@$(cat ./wipeunixtime)" "+%Y/%m/%d %T")"
  fi
  if [ "${ENV_WIPE_CYCLE}" == "monthly" ]; then 
    echo "INFO: ENV_WIPE_CYCLE:monthly"
    echo "INFO: ワイプ周期を1か月に設定します。"
    date -d "+1 month -30 min" +%s > ./wipeunixtime;
    echo "INFO: 次のワイプ予定時刻 -> $(date -d "@$(cat ./wipeunixtime)" "+%Y/%m/%d %T")"
  fi
  if [ ! -f "./wipeunixtime" ]; then
    echo "INFO: ENV_WIPE_CYCLE:未指定または未定義の値"
    echo "INFO: ワイプ周期を1か月に設定します。"
    date -d "+1 month -30 min" +%s > ./wipeunixtime;
    echo "INFO: 次のワイプ予定時刻 -> $(date -d "@$(cat ./wipeunixtime)" "+%Y/%m/%d %T")"
  fi
fi

# update rustdedicated
steamcmd +login anonymous +force_install_dir /root/rustserver +app_update 258550 validate +quit

# exitnode 指定があるなら tailscale を起動 (特権モードが必要)
if [ ! -z "${ENV_TS_EXITNODE_IP}" ]; then
  tailscaled -verbose 1 &
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
        +server.description "${ENV_SERVERDESCRIPTION:=Welcome!\n---\nこのサーバーは現在テスト中です。}\nワイプ予定時刻:$(date -d "@$(cat ./wipeunixtime)" '+%Y/%m/%d %T')" \
        +server.logoimage "${ENV_SERVERLOGOIMG:=https://github.com/user-attachments/assets/9cb873a1-b0c8-4d01-9dfc-df41bb2468e5}" \
        +server.url "${ENV_SERVERURL:=https://github.com/sakkuntyo/docker-rust-server}" \
        +server.seed "$(cat ./seed)" \
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
for ((i = 1; i <= 10; i++))
do
  echo "INFO: $(((10 - i))) 分後にヘルスチェックを開始します。。。"
  sleep 60
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
  fi
  
  ## STOP TIME が設定されている場合だけ停止時間チェック
  if [ ! -z "${ENV_STOP_TIME}" ];then
    echo "DEBUG: --------------------"
    echo "DEBUG: 現在時刻: $(date '+%Y/%m/%d %T')"
    echo "DEBUG: 停止時刻: $(date -d @${TARGET_STOP_UNIXTIME} '+%Y/%m/%d %T')"
    echo "DEBUG: 現在時刻 > 停止時刻 = $(if [[ $(date '+%s') -gt "$(date -d @${TARGET_STOP_UNIXTIME} '+%s')" ]] ; then echo true; else echo false; fi)"
    echo "DEBUG: --------------------"

    # 停止する時刻を過ぎたなら停止
    if [[ "$(date +%s)" -gt "${TARGET_STOP_UNIXTIME}" ]]; then
      echo "INFO: 停止時刻となったため停止します。"
      rcon -t web -a 127.0.0.1:${ENV_RCON_PORT:=28016} -p "${ENV_RCON_PASSWD:=StrongPasswd123456}" "global.say 再起動します";
      sleep 10
      kill 1
    # 1 時間前ならアナウンス
    elif [ -z ${REBOOTMSG_1HOUR_SENT_FLG} ] && [[ "$(date +%s)" -gt "$(date -d "@${TARGET_STOP_UNIXTIME}" -d "-1 hour" +%s)" ]]; then
      echo "INFO: 停止時刻1時間前になりました。"
      rcon -t web -a 127.0.0.1:${ENV_RCON_PORT:=28016} -p "${ENV_RCON_PASSWD:=StrongPasswd123456}" "global.say 1時間後に再起動します。/ Server will restart in an hour.";
      REBOOTMSG_1HOUR_SENT_FLG=true
    # 30分前ならアナウンス
    elif [ -z ${REBOOTMSG_30MIN_SENT_FLG} ] && [[ "$(date +%s)" -gt "$(date -d "@${TARGET_STOP_UNIXTIME}" -d "-30 minutes" +%s)" ]]; then
      echo "INFO: 停止時刻30分前になりました。"
      rcon -t web -a 127.0.0.1:${ENV_RCON_PORT:=28016} -p "${ENV_RCON_PASSWD:=StrongPasswd123456}" "global.say 30分後に再起動します。/ Server will restart in 30 minutes.";
      REBOOTMSG_30MIN_SENT_FLG=true
    # 15分前ならアナウンス
    elif [ -z ${REBOOTMSG_15MIN_SENT_FLG} ] && [[ "$(date +%s)" -gt "$(date -d "@${TARGET_STOP_UNIXTIME}" -d "-15 minutes" +%s)" ]]; then
      echo "INFO: 停止時刻15分前になりました。"
      rcon -t web -a 127.0.0.1:${ENV_RCON_PORT:=28016} -p "${ENV_RCON_PASSWD:=StrongPasswd123456}" "global.say 15分後に再起動します。/ Server will restart in 15 minutes.";
      REBOOTMSG_15MIN_SENT_FLG=true
    # 5分前ならアナウンス
    elif [ -z ${REBOOTMSG_5MIN_SENT_FLG} ] && [[ "$(date +%s)" -gt "$(date -d "@${TARGET_STOP_UNIXTIME}" -d "-5 minutes" +%s)" ]]; then
      echo "INFO: 停止時刻5分前になりました。"
      rcon -t web -a 127.0.0.1:${ENV_RCON_PORT:=28016} -p "${ENV_RCON_PASSWD:=StrongPasswd123456}" "global.say 5分後に再起動します。/ Server will restart in 15 minutes.";
      REBOOTMSG_5MIN_SENT_FLG=true
    fi
  fi

  sleep 60
done
