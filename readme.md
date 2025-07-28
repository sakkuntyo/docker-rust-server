# 環境変数
以下の環境変数をコンテナ作成時に指定できます。指定しない場合はデフォルト値が指定されます。

|変数名|既定値|概要|
|:-:|:-:|:-:|
|ENV_SERVER_PORT|28015||
|ENV_RCON_PORT|28016||
|ENV_RCON_PASSWD|StrongPasswd123456|既定値は非推奨|
|ENV_WIPE_CYCLE|monthly|monthly<br>bi-weekly<br>weekly<br>daily|
|ENV_WORLDSIZE|3000|3000 - 6000|
 
以下は tailscale exitnode を使用する場合に必要
- ENV_TS_EXITNODE_IP
  - 指定する場合、コンテナの Privileged モードの有効化が必要
- ENV_TS_HOSTNAME
  - tailscale ネットワーク上で表示される名前、未指定だとコンテナIDになる
- ENV_TS_AUTHKEY
  - 非対話で進めたい場合に指定する

※ サーバーリストに表示される条件がある
- +sv.secure +sv.EAC が有効である必要アリ(なので明示的に有効指定しています)
- 初回の RustDedicated 起動時に steam へ報告されたグローバルIP と プレイヤーがアクセスするIPが一致しなければいけない
  - ポートフォワードやリバースプロキシによる別のグローバルIPからのアクセスは恐らく不可
