const STATUS_TEXT = {
    'mode-enabled': [
        '設定時にONを確認',
        '以後の手動変更は自動監視していません。最新確認は「ホストを確認」を押してください。学習停止の実効性は未検証です。',
    ],
    idle: ['待機中', '自動設定の対象となる窓はありません。'],
    unavailable: ['設定を確認できません', '保護は確認できていません。設定と接続を確認してください。'],
    'recovery-needed': ['復元の確認が必要です', '全ブラウザのプライベート窓を閉じ、復元を実行してください。'],
    'not-configured': ['初期設定が必要です', 'Windowsホストの設定手順を完了してください。'],
};

const DETAIL_TEXT = {
    'active-requests-prevent-restore': [
        '復元できません', '連動要求が残っています。全ブラウザのプライベート窓を閉じてください。',
    ],
    'pre-existing-or-unknown-mode-preserved': [
        'IME設定を維持しました', '開始前からON、または変更元が不明です。必要ならGoogle日本語入力の設定で解除してください。',
    ],
    'explicit-restore-confirmed': ['元の設定へ復元しました', 'シークレット設定がOFFになったことを確認しました。'],
    'already-disabled': ['シークレット設定はOFFです', '復元のための変更は不要でした。'],
    'potential-change-needs-review': [
        'IME設定の確認が必要です', '前回の設定変更が残っている可能性があります。「ホストを確認」で状態を確認してください。',
    ],
    'restore-prerequisites-unconfirmed': [
        '復元を中止しました', 'プライベート窓がないことを確認できません。窓と実行許可を確認して再試行してください。',
    ],
};

const MANUAL_RECOVERY_DETAILS = new Set([
    'ownership-unknown-use-ime-settings', 'previous-session-needs-manual-review',
    'restore-unconfirmed-use-ime-settings', 'enable-unconfirmed-use-ime-settings',
]);

export function describeView(view) {
    if (!view?.status) throw new Error('invalid-status');
    const {state, detail} = view.status;
    if (DETAIL_TEXT[detail]) return DETAIL_TEXT[detail];
    if (MANUAL_RECOVERY_DETAILS.has(detail)) {
        return ['IME設定で復旧してください', '状態や変更元を確認できません。Google日本語入力の公式設定で確認・復旧してください。'];
    }
    if (!view.enabled && state === 'idle') {
        return ['自動設定は無効です', '有効にする前に、下の説明を確認してください。'];
    }
    if (detail === 'incognito-permission-required') {
        return ['プライベート実行の許可が必要です', 'ブラウザの拡張機能詳細で許可してください。'];
    }
    return STATUS_TEXT[state] ?? STATUS_TEXT.unavailable;
}
