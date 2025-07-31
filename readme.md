# 概要

Rust サーバーを管理するために生まれたコンテナです。
以下がこのコンテナ一つで出来ます。

- 指定したワイプ時刻に日次停止(コンテナを unless-stopped にすることで再起動されます)
- 再起動時にワイプ期間が過ぎていればワイプを実行
- 再起動時にRustDedicatedのアップデート
- netstat によるポートと pgrep によるプロセスのヘルスチェック
- tini, trap, rcon-cli による docker stop 時の自動セーブ
- tailscale exitnode の使用(Privileged モードが必要です)

# 使い方

```
docker run sakkuntyo/rust-server:latest -e ENV_SERVERNAME="サーバー名" -e ENV_SERVERDESCRIPTION=welcome!
```

DockerHub: https://hub.docker.com/r/sakkuntyo/rust-server

## 環境変数
RustDedicated のオプションを環境変数で指定できます。指定しない場合はデフォルト値が指定されます。

|変数名|既定値|概要|
|:-|:-|:-:|
|ENV_SERVERNAME|TEST SERVER|サーバーリストに表示される|
|ENV_SERVERDESCRIPTION|Welcome!\n---\nこのサーバーは現在テスト中です。|ここで指定した内容の後にサーバー設定が記載される|
|ENV_SERVERURL|https://github.com/sakkuntyo/docker-rust-server| Discord のリンクやHPに置き換える |
|ENV_SERVERLOGOIMG|https://github.com/user-attachments/assets/9cb873a1-b0c8-4d01-9dfc-df41bb2468e5||
|ENV_SERVERTAGS|Vanilla|https://wiki.facepunch.com/rust/server-browser-tags|
|ENV_WORLDSIZE|3000|3000 - 6000|
|ENV_SEED||未指定では初回起動時のunixtime|
|ENV_MAXPLAYERS|100|サーバー最大人数|
|ENV_MAXTEAMSIZE|8|パーティ最大人数|
|ENV_WIPE_TYPE|FULL|monthly<br>FULL または MAP|
|ENV_WIPE_CYCLE|monthly|monthly<br>bi-weekly<br>weekly<br>daily<br>* monthlyは5週間|
|ENV_WIPE_DAY_OF_WEEK||sunday<br>monday<br>tuesday<br>wednesday<br>thursday<br>friday<br>saturday|
|ENV_WIPE_TIME|09:00|20:00 を指定すると、ワイプ時刻が 20:00 になります。<br>それと並行し毎日 20:00 に停止する様になります。(コンテナの自動起動オプションと合わせて再起動)|
|ENV_SERVER_PORT|28015||
|ENV_RCON_PORT|28016||
|ENV_QUERY_PORT|28017||
|ENV_RCON_PASSWD|StrongPasswd123456|既定値は非推奨|

以下は tailscale exitnode を使用する場合に必要です。

|変数名|既定値|概要|
|:-|:-|:-:|
|ENV_TS_EXITNODE_IP||指定する場合、コンテナの Privileged モードの有効化が必要|
|ENV_TS_HOSTNAME||tailscale ネットワーク上で表示される名前、未指定だとコンテナIDになる|
|ENV_TS_AUTHKEY||非対話で進めたい場合に指定する|

## tailscale exitnode オプションの用途
リバースプロキシ環境を挟んだ時の以下 3 の問題を解決するために tailscale exitnode を使う事にしました。
ただ、ほとんどの人には不要なオプションかもしれません。

1. Rust サーバーマシンの IP を知られたくないので、フロントに別の IP を持つ中継サーバー(ポートフォワードやリバースプロキシ)を挟んだ所、プレイヤーがサーバー接続時に Steam Auth Failed が発生して接続できませんでした。(Steam Auth Failed の原因は様々ある事に注意)
2. 理由としては初回の RustDedicated 起動時にサーバーから Steam に グローバルIP が登録されていること。(Steam が GSLT を発行するついでに IP を据え置いてると思われます。
3. Rust サーバー初回起動時にサーバーが Steam と通信したグローバルIP と プレイヤーがサーバーにアクセスする時のIP(フロントの中継サーバー)が一致しない場合、プレイヤーがサーバーへ接続しようとした時に Steam Auth Failed が発生する様です。
   - Rust サーバーマシン => Steam (IP は A.A.A.A か... メモメモ)
   - Rust サーバーマシン <= 中継サーバー <= プレイヤー (IP B.B.B.B に接続... Steam Auth Failed!)
4. Rust サーバーマシンは、フロントに置いた中継サーバーを exitnode にしていれば、Rust サーバーマシンが中継サーバーのグローバル IP で Steam と通信し、その IP が Steam に登録されるため、プレイヤーは中継サーバーの IP で接続でき、Rust サーバーマシン は IP を知られずに済みます。
   - Rust サーバーマシン => 中継サーバー => Steam (IP は B.B.B.B か... メモメモ)
   - Rust サーバーマシン <= 中継サーバー <= プレイヤー (IP B.B.B.B に接続... 接続成功!)


## .env サンプルとイメージ

```
ENV_SERVERNAME=nomarust Med | JP | Vanilla | Bi-weekly
ENV_SERVERDESCRIPTION=Welcome! nomarust server!
ENV_SERVERURL="https://discord.gg/C8kSMRNS"
ENV_SERVERLOGOIMG="https://github.com/user-attachments/assets/e118e9d5-8bdc-489c-837b-f516def3f55c"
ENV_SERVERTAGS=Vanilla,bi-weekly,EA
ENV_WORLDSIZE=3900
ENV_MAXPLAYERS=150
ENV_MAXTEAMSIZE=4
ENV_WIPE_CYCLE=bi-weekly
ENV_WIPE_TIME=09:00
ENV_WIPE_DAY_OF_WEEK=monday
ENV_SERVER_PORT=30015
ENV_RCON_PORT=30016
ENV_QUERY_PORT=30017
ENV_RCON_PASSWD=秘密
ENV_TS_EXITNODE_IP=秘密
ENV_TS_HOSTNAME=docker-rust-med
ENV_TS_AUTHKEY=秘密
```

<img width="403" height="384" alt="image" src="https://github.com/user-attachments/assets/9a42d07e-2c67-4f7a-9f77-d1e0fd1ef87f" />




