# ADR-001: フロントエンド技術選定

## ステータス
決定

## コンテキスト
WebUIの実装技術としてBlazor Server、Vue3、Angularが候補に挙がった。

## 決定
Blazor Serverを採用する。Vue3・Angularは不採用とする。

## 理由
- ASP.NET Coreバックエンドとの親和性が高い。
- 単一ビルドパイプラインでRPM/DEBに配布しやすい（フロントエンド用の別ビルドパイプラインを持つコストを回避）。
