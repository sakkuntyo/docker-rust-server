#!/bin/bash
if [ ! -f "./seed" ] then date +%Y%m%d%H%M%S > ./seed
seed=$(cat ./seed)

#update rustdedicated
steamcmd +login anonymous +force_install_dir /root/rustserver +app_update 258550 validate +quit

tailscaled -verbose 1 &
tailscale status && {
  tailscale up --exit-node="${ENV_TS_EXITNODE_IP}" --hostname=${ENV_TS_HOSTNAME}
  :
} || {
  tailscale up --auth-key=${ENV_TS_AUTHKEY} --exit-node="${ENV_TS_EXITNODE_IP}" --hostname=${ENV_TS_HOSTNAME}
  :
}


./RustDedicated -batchmode \
        +server.identity "serverdata1" \
        +server.hostname "Test | JP | Vanilla | Monthly" \
        +server.description "Welcome!\n---\ndiscord: https://discord.gg/nAyqFErqV4\n---\nこのサーバーは現在、運用テスト中です。\n詳細は Discord から確認してください。\n最大チーム人数:未定\nマップサイズ:未定\nワイプ予定日:未定" \
        +server.logoimage "https://github.com/user-attachments/assets/9cb873a1-b0c8-4d01-9dfc-df41bb2468e5" \
        +server.url "https://discord.gg/Wr6yunTY" \
        +server.seed "${seed}" \
        +server.worldsize ${ENV_WORLDSIZE:=3000} \
        +server.maxplayers 100 \
        +server.maxconnectionsperip 500 \
        +app.maxconnectionsperip 500 \
        +relationshipmanager.maxteamsize 24 \
        +sv.secure 1 \
        +sv.EAC 1 \
        +rcon.password "${ENV_RCON_PASSWD:=StrongPasswd123456}" \
        +server.port ${ENV_SERVER_PORT:=28015} \
        +rcon.port ${ENV_RCON_PORT:=28016} \
        +server.queryport ${ENV_QUERY_PORT:=28017} \
        +server.tags "PVP,Vanilla" &

# 5分後に死活監視を開始
for ((i = 1; i <= 600; i++))
do
  echo "$(date):$(((600 - i))) 秒後に死活監視を開始します。。。"
  sleep 1
done

while true; do
  echo "chacking pgrep tailscaled..."
  pgrep tailscaled > /dev/null && {
    echo "-> ok"
    echo "chacking pgrep RustDedicated..."
    pgrep RustDedicated > /dev/null && {
      echo "-> ok"
      echo "chacking netstat -tuln | grep 28015..."
      netstat -tuln | grep 28015 > /dev/null && {
        echo "-> ok"
        # ポートのリッスンもされてるのでもんだいなし！
        :
      } || {
        # ポートがリッスンされてないのでコンテナ停止(コンテナ自動起動オプションで再起動させたい)
        echo "$(date):ポート28015 のリッスンがないためコンテナを停止します(必要に応じてコンテナ自動起動オプションを使用してください)"
        kill 1
        :
      }
    } || {
      #Rust自体起動してないのでコンテナ停止(コンテナ自動起動オプションで再起動させたい)
      echo "$(date):RustDedicate が起動していないのでコンテナを停止します(必要に応じてコンテナ自動起動オプションを使用してください。)"
      kill 1
      :
    } || {
      #tailscaled が起動してないのでコンテナ停止(コンテナ自動起動オプションで再起動させたい)
      echo "$(date):tailscaled が起動していないのでコンテナを停止します(必要に応じてコンテナ自動起動オプションを使用してください。)"
      kill 1
      :
    }
  };
  sleep 30
done
