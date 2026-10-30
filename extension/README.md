# SecretIMEのブラウザ拡張

Manifest V3の依存パッケージ不要の拡張である。Chrome・Edge・Brave・Vivaldiを同じ実装の対象とするが、実機検証済みの対応宣言ではない。

開発版の拡張IDは`djbbbndacfniipaiopkjbeipamnjaneo`である。manifestのkeyは、この開発版のIDを固定するRSA公開鍵だけを含む。秘密鍵を保存していないため、このkeyをストア署名鍵や更新署名鍵としては使わない。

ブラウザの拡張管理画面からこのフォルダーを「パッケージ化されていない拡張機能」として読み込み、プライベート窓での実行を許可する。ホスト名は`jp.secretime.host`である。Windowsホストの設定を別途完了し、popupの説明を読んで自動設定を有効にする。初期状態では無効である。

通常時にはホストを起動しない。窓イベントでprivate窓の存在を再確認し、private窓がある間だけnative portを維持する。最後の窓を閉じたら`sync active:false`の応答を待ってportを閉じる。IME設定は自動解除しない。全ブラウザでprivate窓を閉じてから復元ボタンを使う。ホストのエラーや切断後は同一privateセッション中に自動再接続せず、「窓の状態を再確認」で再試行する。

「ホストを確認」と「復元」は利用者のクリックでだけ実行する。ホスト応答待ちは最大45秒である。非活動時に定期ポーリングはしない。SETは設定時の確認であり、以後の手動変更を自動監視しない。現在のモードを再読取するには「ホストを確認」を押す。学習停止の実効性や入力開始前の適用は保証しない。

モードの確認結果とホストへのlease登録は分けて扱う。`sync(true)`でlease登録を確認したら、モードが不明でもprivate窓がある間は接続を維持する。復元の直前にはprivate窓と実行許可を再照会し、private窓がある場合、許可がない場合、照会に失敗した場合はホストへ復元を送らない。ほかのブラウザの要求が残る場合はホストでも拒否する。照会後に新しい窓を開く操作と復元の原子的な同期を保証する機構はないため、復元中もprivate窓を開かないこと。

設定要求の送信前に変更可能性を示す真偽値を永続化し、途中でブラウザが異常終了しても次回起動時に確認を案内する。OFFを実際に確認するまでこの印を消さない。ホストの最後の状態も固定コード・真偽値・件数として保存する。入力内容・URL・閲覧履歴・窓IDは保存しない。起動時の案内のためにホストや設定画面を自動起動しない。開始前からONだった場合にも確認を案内することがある。

権限は`nativeMessaging`と`storage`だけである。[Chrome公式windows API](https://developer.chrome.com/docs/extensions/reference/api/windows)に従い、`getAll({populate:false})`でincognitoフラグを使う。URL等のタブ情報が不要なのでtabs権限を要求しない。[spanning](https://developer.chrome.com/docs/extensions/reference/manifest/incognito)により通常窓とprivate窓を単一ワーカーで扱う。[Native Messaging](https://developer.chrome.com/docs/extensions/develop/concepts/native-messaging)の外部通信先は登録済みローカルホストだけである。

機能テストはリポジトリ直下で`node --test tests/extension.test.mjs`を実行する。モックChromeによる試験を実ブラウザ・実IME試験の成功として扱わない。
