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
  echo "INFO: 現在の時刻　　 -> $(date "+%Y/%m/%d %T")"
  echo "INFO: ワイプ予定時刻 -> $(date -d "@$(cat ./server/wipeunixtime)" "+%Y/%m/%d %T")"
  echo "INFO: ワイプの種類   -> ${ENV_WIPE_TYPE:=FULL}"
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
echo "INFO: 定期停止時刻: ${ENV_WIPE_TIME:=09:00}"
echo "INFO: 次の停止時刻: $(date -d "@${TARGET_STOP_UNIXTIME}" '+%Y/%m/%d %T')"

# 初回起動時に現在時刻(unixtime)のseed値と作成日時
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
  
  echo "INFO: ENV_WIPE_CYCLE:${ENV_WIPE_CYCLE}"
  date -d "$(echo "${ENV_WIPE_DAY_OF_WEEK} ${ENV_WIPE_CYCLE_DATED} ${ENV_WIPE_TIME}" | sed "s/.* 1 days/1 days/g" | sed "s/day 1 week/day 0 week/g" | sed "s/day 2 week/day 1 week/g" | sed "s/day 5 week/day 4 week/g")" +%s > ./server/wipeunixtime;
  echo "INFO: ワイプ予定時刻 -> $(date -d "@$(cat ./server/wipeunixtime)" "+%Y/%m/%d %T")"
fi

# update rustdedicated
steamcmd +login anonymous +force_install_dir /root/rustserver +app_update 258550 validate +quit

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
        +server.description "${ENV_SERVERDESCRIPTION:=Welcome!}\n---\nMax team size:${ENV_MAXTEAMSIZE:=8}\nMax players:${ENV_MAXPLAYERS:=100}\nWorld size:${ENV_WORLDSIZE:=3000}\nWipe schedule:${ENV_WIPE_CYCLE:=Monthly}\nWipe Type: ${ENV_WIPE_TYPE:=FULL}\nNext wipe:$(date -d "@$(cat ./server/wipeunixtime)" '+%Y-%m-%d_%T(%Z)')\nNext restart/stop time:$(date -d "@${TARGET_STOP_UNIXTIME}" '+%Y-%m-%d_%T(%Z)')" \
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
    rcon -t web -a 127.0.0.1:${ENV_RCON_PORT:=28016} -p "${ENV_RCON_PASSWD:=StrongPasswd123456}" "global.say サーバーは5分後に停止/再起動されます。/ Server will restart or stop in 15 minutes.";
    REBOOTMSG_5MIN_SENT_FLG=true
  fi

  sleep 60
done
