#!/bin/bash
tailscaled -verbose 1 &
./RustDedicated -batchmode \
        +server.identity "server3500.20250722" \
        +server.hostname "nomarust Japan | JP | Vanilla | Monthly" \
        +server.description "Welcome to nomarust!\n---\ndiscord: https://discord.gg/nAyqFErqV4\n---\nこのサーバーは現在、運用テスト中です。\n詳細は Discord から確認してください。\n最大チーム人数:24 (同盟を許可しません)\nマップサイズ:3500\nワイプ予定日:2025/8/7 18:00 UTC\n---\nThis server is currently undergoing operational testing.\n Please check Discord for more details.\nTeam limit:24 (not allowed alliance)\nMap size:3500\nNext wipe schedule: 2025/8/7 18:00 UTC" \
        +server.logoimage "https://github.com/user-attachments/assets/9cb873a1-b0c8-4d01-9dfc-df41bb2468e5" \
        +server.url "https://discord.gg/Wr6yunTY" \
        +server.seed "20250722" \
        +server.worldsize 3500 \
        +server.maxplayers 100 \
        +server.maxconnectionsperip 500 \
        +app.maxconnectionsperip 500 \
        +relationshipmanager.maxteamsize 24 \
        +sv.secure 1 \
        +sv.EAC 1 \
        +rcon.password "" \
        +server.port 28015 \
        +rcon.port 28016 \
        +server.queryport 28017 \
        +server.tags "PVP" \
        +server.tags "JP" \
        +server.tags "Monthly" &

# 5分後に死活監視を開始
for ((i = 1; i <= 300; i++))
do
  echo "$(date):$(((300 - i))) 秒後に死活監視を開始します。。。"
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
