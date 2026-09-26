# 0004: SystemAgentの実行ユーザーとコンテナの実行形態 への回答

- 回答日: (未記入)

質問は[../questions/0004-execution-user.md](../questions/0004-execution-user.md)を参照。暫定方針のままで良い項目は「OK」だけで構いません。

## A1（Q1: 実行ユーザー）
OK。Rootで良い。

## A2（Q2: rootful/rootless）
rootでOK。rootfulとする。このアプリはrootで立ち上げるからね。unitファイルでroot起動する前提で作成しているよ。キミはUnitファイルの作成をする必要はないから安心してね。

## A3（Q3: Podman/Docker併存）
OK。Podman優先で良い。Dockerを使う場合は、`Container:Runtime`を`docker`に設定すれば良い。
そもそも、どっちも入っている環境は少ないと思うので、Podman優先で良いと思う。Dockerを使う場合は、`Container:Runtime`を`docker`に設定すれば良い。
