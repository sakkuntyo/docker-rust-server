# AGENTS.md

## Local files

- `AGENTS.local.md` はローカル運用メモ用のファイルであり、この公開リポジトリには含めない。
- コードオーナー向けの運用メモは、private repository の [AGENTS.local.md](https://github.com/sakkuntyo/docker-rust-server-ops/blob/main/AGENTS.local.md) を参照する。
- ローカル環境固有の本番コンテナ名、Docker push 手順、個人環境のパス、削除してよいボリュームの判断は `AGENTS.local.md` に書く。

## Release運用

- main に変更を入れたら、必要に応じて GitHub Release を更新する。
- コンテナ内の起動スクリプトは [sakkuntyo/docker-rust-server](https://github.com/sakkuntyo/docker-rust-server) にある。
- コンテナリポジトリは [sakkuntyo/rust-server](https://hub.docker.com/repository/docker/sakkuntyo/rust-server) にある。
- dockerfile や launch.sh の更新を含むコミットをするたびに新しいバージョン Tag と Release を作成する。
- Tag は w.x.y 形式で、y だけを更新する。
- w.x はユーザー操作で更新する。
- Docker Hub digest を Release note に追記する。

## Rust server運用

- Community 掲載を維持するため、uMod/Oxide の `Options.Modded=false` は維持する。
- 管理系 plugin は admin 用途に限定し、gameplay 変更 plugin は追加前に確認する。
- `ENV_OWNERIDS` の権限付与は status file で冪等化する。
