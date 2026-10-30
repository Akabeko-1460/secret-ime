# Native Messaging と保護状態の契約

ホスト名は `jp.secretime.host` とする。通信内容はプロトコル版、操作、private窓の有無だけである。
URL、入力文字列、候補、閲覧履歴、ブラウザが指定したクライアントIDを受け付けない。
クライアントの識別と接続終了処理は、同一ユーザーのブローカーが接続単位で行う。

## メッセージは最大2048バイトに制限する

標準入出力のフレームは4バイトの符号なしリトルエンディアン長とUTF-8 JSON本文から成る。
長さ0、2048バイト超、途中EOF、無効UTF-8を拒否する。ヘッダー読取前のEOFだけを正常終了とする。
標準出力にはフレーム以外を書かない。JSONは単一の平坦なオブジェクトとし、重複キー、未知キー、追加値を拒否する。

要求は次の3種類だけを許可する。`v` は整数の `1`、`active` はJSON真偽値だけを許可する。
`status` と `restore` に `active` は付けない。

```json
{"v":1,"op":"sync","active":true}
{"v":1,"op":"status"}
{"v":1,"op":"restore"}
```

`sync` は当該接続のleaseを上書きする。同じ `true` の再送は要求数を増やさない。
複数ブラウザ・プロファイルの要求を集合で保持し、一接続の終了は他接続の要求を消さない。
Coreの同時クライアント上限は64である。Coreの状態遷移とアダプター呼出しは同一ロックで直列化する。

応答は次のキーを持つ。`modeEnabled` は `true`、`false`、状態不明時の `null` を区別する。

```json
{"v":1,"state":"mode-enabled","detail":"mode-effectiveness-unverified","effectiveness":"unverified","restoreRequired":false,"activeClients":1,"modeEnabled":true}
```

`state` は `mode-enabled`、`idle`、`unavailable`、`recovery-needed`、`not-configured` のいずれかである。
`mode-enabled` はUI上のモード設定の確認であり、IMEの永続学習停止を証明しない。
そのため `effectiveness` は常に `unverified` とする。`detail` は固定の機械可読コードで、入力を含まない。

## 初期版は最後の窓を閉じても自動OFFにしない

`IPrivacyAdapter.Read()` はON/OFF/不明を返す。`Enable()` と `Restore()` は操作成功を返すが、Coreは操作後にも再読取する。
アダプター自身も操作直前の状態を確認し、盲目的なトグルを避ける。例外は成功扱いしない。

開始時に既にONなら、その設定の所有権を取得せずOFFに戻さない。
OFFからONへの操作を確認した場合だけ、その実行中セッションの変更として保持する。
途中でOFFや不明を観測したら所有権を失効し、自動で再ONにせず復旧要求を残す。
値の比較だけでは同値への手動変更を区別できないため、自動復元の安全性は保証できない。

最後の要求が終了した場合はONを保ち、明示的な復元操作を求める。
`restore` はUIでの利用者の明示操作からだけ送信し、切断・タイマー・エラー処理から送らない。
残る要求がなく、この実行中セッションが確認した変更の所有権がある場合だけOFFを試みる。
復元失敗後は所有権を失効し、OFFを自動再試行しない。

ブローカーは変更前に復旧マーカーを永続化する。
異常終了を示すdirtyマーカーが残った場合は、`recoveryRequired: true, restoreOwned: false` で開始する。
この場合は前回の変更の所有権を推測せず、`sync(true)` でも変更しない。
ONならIME設定画面での手動復元を案内し、OFFを確認した後の明示 `restore` で復旧要求を解消できる。

正常な最終切断時だけ、ブローカーはモードONと `HasOwnedChange` を確認してclean-ownedマーカーを保存できる。
次回は `recoveryRequired: true, restoreOwned: true` として、明示復元に限って所有権を継承する。
この場合も `sync(true)` は設定を書き換えず、他の要求が残る間は復元しない。
OFF・不明・復元失敗を観測したら継承した所有権を失効する。
`HasOwnedChange` の取得自体はIMEへ問い合わせないため、保存前のモード確認は別途必要である。
停止中の同値への手動変更は識別できない。復元要求は利用者が変更内容を理解した明示操作に限定する。

Coreのテストは状態機械と通信の単体試験である。実機UI、Google日本語入力の学習履歴、ブラウザとのE2E成功を意味しない。
