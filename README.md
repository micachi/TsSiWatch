# TsSiWatch

EDCB の「録画後実行BAT」から呼び出して、録画ファイル（`.m2ts` / 素の TS）の SI（NIT / SDT）を解析し、
BS / 110度CS / DVB-C のトランスポンダ・チャンネルリストの変化を Discord に通知する
.NET Framework 4.8 のコンソールツールです。

録画を1本流すたびにチャンネル構成のスナップショットを TSV に蓄積し、前回と同じネットワーク（ONID）の
最新スナップショットと比較して、差分があるときだけ通知します。

## 特徴

- **ランタイム不要** — Windows 10 / 11 に標準搭載の .NET Framework 4.8 でそのまま動きます
- **高速** — 5.2GB の録画を約 3 秒で走査（OS キャッシュ後）。ストリーミング読み込みなのでメモリも食いません
- **ネットワーク単位で比較** — ONID ごとに別スナップショット・別差分にします
  - `110BS`（ONID 4） / `110`（ONID 6, 7） / `124/128` / `DVB-C`
- **NIT actual のみを実データとして採用** — 1 本の TS に混ざる NIT other（他ネットワークの部分的な写し）を
  比較に混ぜないため、110CS の録画が 110BS の履歴を上書きする事故が起きません
- **検知日付はファイル名の日時** — ファイルの更新日時はコピー・展開・移動で壊れるため使いません
- **変化がなければ何もしない** — TSV も追記しません
- **Discord Webhook / Bot トークン両対応**、`@メンション` 付きで投下
- **実行ログを必ずファイルに残す** — EDCB からは標準出力が見えないため `data\TsSiWatch.log` に記録します

## 動作要件

- Windows 10 / 11（.NET Framework 4.8）
- ビルドする場合のみ Roslyn の `csc.exe`（Visual Studio 2022 Build Tools など）

## ビルド

```bat
"C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe" ^
  -nologo -optimize+ -target:exe -out:TsSiWatch.exe ^
  -r:System.dll -r:System.Core.dll -r:System.Net.Http.dll -r:System.Web.Extensions.dll Program.cs
```

`csc.exe` のパスは環境に合わせてください。生成物は `TsSiWatch.exe` の 1 ファイルです。

## セットアップ

1. `TsSiWatch.exe` と `EDCB録画後実行サンプル.bat` を同じフォルダに置く
   （`config.json` は初回実行時に自動生成されます）
2. `config.json` を編集する

```json
{
  "dataDir": "data",
  "notifyOnFirstRun": false,
  "discord": [
    {
      "name": "main",
      "webhook": "https://discord.com/api/webhooks/XXX/YYY",
      "userIds": ["123456789012345678"]
    }
  ]
}
```

| キー | 内容 |
|---|---|
| `dataDir` | TSV スナップショットの保存先（exe からの相対パス可） |
| `notifyOnFirstRun` | 初回（データ未作成時）も通知するか。`false` 推奨 |
| `discord[].webhook` | Discord の Webhook URL（サーバー設定 → 連携 → ウェブフック） |
| `discord[].userIds` | メンションするユーザー ID。複数可。`[]` でメンションなし |
| `discord[].botToken` + `channelId` | Webhook の代わりに Bot で投下する場合（併用可） |
| `templates` | 通知文言の上書き（キー: `110BS` / `110` / `124/128` / `DVB-C`） |

3. 疎通確認

```bat
TsSiWatch.exe --test-notify
```

4. EDCB の「設定 → 動作設定 → 録画後実行BAT」に `EDCB録画後実行サンプル.bat` を指定

bat は `%~dp0` で自分のフォルダの exe を呼ぶので、パスの書き換えは不要です。

## 使い方

```bat
TsSiWatch.exe "録画.m2ts"                  :: 解析して差分があれば通知
TsSiWatch.exe "録画.m2ts" --dry-run        :: 投下せず内容だけ表示（TSV は更新される）
TsSiWatch.exe --test-notify                :: config.json の Discord 設定にテスト投下
TsSiWatch.exe "録画.m2ts" --config my.json :: 別の設定ファイルを使う
TsSiWatch.exe "録画.m2ts" --quiet          :: 画面出力を抑える（ログは残る）
```

終了コード: `0` = 正常 / `1` = 引数・解析エラー / `4` = Discord 送信失敗。

## 通知の例

```
@yourname
110BSの周波数/チャンネルリストの変更が検知されました
検知時刻: 2026-10-19 17:10
対象: ONID 4 (BS Digital)
トランスポンダ: 追加0 削除0 変更0
サービス: 追加0 削除0 移動0 変更2

[変更SVC] ONID 4 TSID 16625 SID 101: name 繁!!帯 → NHK BS
[変更SVC] ONID 4 TSID 16625 SID 102: name 繁!!帯 → NHK BS
```

長い場合は 1900 文字で丸め、残件数を `… 他 N 件` で示します。

## 分類と保存先

| ラベル | 条件 | 保存先 |
|---|---|---|
| `DVB-C` | cable_delivery_system_descriptor (0x44) がある | `data\DVB-C.tsv` |
| `110BS` | ONID 4（BS デジタル） | `data\110BS.tsv` |
| `110` | ONID 6 / 7（110度CS） | `data\110.tsv` |
| `124/128` | 上記以外（DVB-S など） | `data\124_128.tsv` |

## TSV フォーマット

タブ区切り UTF-8（BOM 付き）。1 行が「スナップショット日付 × チャンネル 1 件」です。

```
date  label  onid  tsid  freq_hz  pol  symrate  modulation
fec_inner  fec_outer  system  service_id  service_name  provider  service_type
```

比較は「同じ ONID の、最も新しい日付のスナップショット」と入力録画の間で行います。
Excel で開いて履歴を直接見ることもできます。

## 動作の詳細

- **NIT actual / NIT other** — 1 本の TS には両方が入ります。NIT other は他ネットワークの部分的な写しで、
  たとえば 110CS の録画にも BS（ONID 4）の TP が 1 本だけ載ります。そのため実データは NIT actual (0x40)
  由来の TP だけを採用し、さらに ONID ごとに別ファイル・別比較にしています。
- **差分の種類** — トランスポンダの追加 / 削除 / 変更（周波数・偏波・シンボルレート・変調・FEC・方式）、
  サービスの追加 / 削除 / 移動（同一 ONID・同一 SID で TSID が変わったもの）/ 変更（名前・提供者・種別）。
- **名前の引き継ぎ** — 短い録画では SDT が揃わずサービス名が空になることがあるため、前回スナップショットの
  名前を引き継ぎます（「名前 → 空」の誤検知を防ぐ）。
- **検知日付** — ファイル名の `YYYY_MM_DD_HH_MM`（EDCB の命名）を優先します。ファイル名に日時が無いときだけ
  放送波の時刻（TDT/TOT）、それも無ければ実行時刻を使います。放送波の時刻がファイル名の日時と 3 時間以上
  ずれている場合は `[注意]` をログに出します。

## 既知の挙動・制限

- `--dry-run` でも TSV は更新されます（差分の基準が進みます）
- Discord への送信に失敗してもスナップショットは進みます。その変更の通知は失われますが、
  `data\TsSiWatch.log` に HTTP ステータスと Discord の応答本文が残ります
- 比較基準は「同じ ONID の最新日付」なので、古い録画を後から流すとそのスナップショットは基準になりません
- スナップショットの日付は分単位です。同じ分に 2 本録ると 1 スナップショットに融合します
- 文字デコードはヒューリスティクスを含みます。半角 ASCII だけの偶数長の名前は 2 バイト漢字と誤認され得ます
  （実測した BS / CS / DVB-C のデータでは発生していません）
- 旧バージョンが 1 ファイルに混在させたスナップショットは、起動時に自動で整理します。変更したファイルは
  `<name>.<日時>.bak` に退避されます（何度実行しても安全）
- Discord へは TLS 1.2 以上で接続します

## ライセンス

MIT License（[LICENSE](LICENSE)）
