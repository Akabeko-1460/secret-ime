using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

namespace SecretIme.Setup
{
    internal static class Program
    {
        [STAThread]
        public static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using (var mutex = new Mutex(false, @"Local\SecretIME.Setup"))
            {
                bool acquired;
                try { acquired = mutex.WaitOne(0); }
                catch (AbandonedMutexException) { acquired = true; }
                if (!acquired) { MessageBox.Show("別のSecretIMEセットアップを閉じてください。", "SecretIME"); return; }
                try
                {
                    if (!Environment.Is64BitOperatingSystem)
                        throw new IOException("Windows 11 x64で実行してください。");
                    Application.Run(new SetupForm());
                }
                catch (Exception error) { MessageBox.Show(error.Message, "SecretIME", MessageBoxButtons.OK, MessageBoxIcon.Error); }
                finally { mutex.ReleaseMutex(); }
            }
        }
    }

    internal sealed class SetupForm : Form
    {
        private readonly Installer installer;
        private readonly CheckBox consent;
        private readonly TextBox status;
        private readonly Button install;
        private readonly Button copy;
        private readonly Button folder;

        public SetupForm()
        {
            string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "SecretIME");
            installer = new Installer(root, new CurrentUserRegistrations());
            Text = "SecretIME セットアップ";
            Font = new Font("Yu Gothic UI", 10);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(700, 600);
            MinimumSize = new Size(620, 600);
            StartPosition = FormStartPosition.CenterScreen;
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(22), ColumnCount = 1, RowCount = 7 };
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(layout);
            layout.Controls.Add(new Label { Text = "SecretIMEをこのユーザーに導入", Font = new Font(Font.FontFamily, 16, FontStyle.Bold), AutoSize = true, Margin = new Padding(0, 0, 0, 12) });
            layout.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(640, 0), Text =
                "Windows 11 x64・Google日本語入力向けの試験版です。学習停止とブラウザ互換性は未検証です。\n" +
                "追加ダウンロード・管理者権限は不要です。IME設定はセットアップ中に変更しません。\n" +
                "導入先: " + root + "\n更新時は全ブラウザのSecretIME連動を無効にし、必要な復元を済ませてください。" });
            consent = new CheckBox { AutoSize = true, MaximumSize = new Size(640, 0), Margin = new Padding(0, 12, 0, 12), Text =
                "未検証の試験版と理解し、拡張の連動を有効にした後の公式設定画面の操作を許可する" };
            layout.Controls.Add(consent);
            status = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill,
                Text = "1. 上の確認欄を選び、「導入する」を押します。\r\n2. 導入後、ここにブラウザ側の手順を表示します。\r\n\r\n設定操作が有効になるのは、拡張側でも連動をONにした後です。" };
            layout.Controls.Add(status);
            var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 12, 0, 0) };
            install = new Button { Text = "導入する", AutoSize = true, Enabled = false };
            install.Click += delegate { Install(); };
            consent.CheckedChanged += delegate { install.Enabled = consent.Checked; };
            copy = new Button { Text = "拡張フォルダーをコピー", AutoSize = true, Enabled = false };
            copy.Click += delegate { TryAction(delegate { Clipboard.SetText(installer.ExtensionDirectory); }); };
            folder = new Button { Text = "導入先を開く", AutoSize = true, Enabled = false };
            folder.Click += delegate { TryAction(delegate { Process.Start(new ProcessStartInfo(root) { UseShellExecute = true }); }); };
            actions.Controls.AddRange(new Control[] { install, copy, folder });
            layout.Controls.Add(actions);
            layout.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(640, 0), Margin = new Padding(0, 8, 0, 8),
                Text = "全private窓を閉じても自動OFFにはなりません。通常入力に戻すときは拡張の「復元」を押します。" });
            var remove = new Button { Text = "登録を解除する（ファイルは保持）", AutoSize = true, Anchor = AnchorStyles.Left };
            remove.Click += delegate {
                if (MessageBox.Show("全ブラウザの連動を無効にし、private窓を閉じましたか？\nIME設定の復元も先に確認してください。\n登録だけを解除し、ファイル・復旧情報は保持します。", "登録解除", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
                TryAction(delegate { installer.Unregister(); status.Text = "登録を解除しました。ブラウザから拡張を削除できます。\r\nファイル・復旧情報は保持しています。\r\nバックアップ: " + installer.LastBackup; });
            };
            layout.Controls.Add(remove);
        }

        private void TryAction(Action action)
        {
            try { action(); }
            catch (Exception error) { MessageBox.Show(error.Message, "SecretIME", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }

        private void Install()
        {
            install.Enabled = false;
            TryAction(delegate {
                string current = Assembly.GetExecutingAssembly().Location;
                string maintenance = Path.Combine(Path.GetDirectoryName(installer.ExtensionDirectory), "SecretIME.Setup.exe");
                byte[] executable = String.Equals(current, maintenance, StringComparison.OrdinalIgnoreCase) ? null : File.ReadAllBytes(current);
                using (Stream payload = Assembly.GetExecutingAssembly().GetManifestResourceStream("SecretIME.Payload"))
                    installer.Install(payload, executable);
                copy.Enabled = folder.Enabled = true;
                status.Text = "導入しました。続けて、使うブラウザごとに操作してください。\r\n\r\n" +
                    "1. アドレス欄に次の管理画面を入力します。\r\nChrome: chrome://extensions\r\nEdge: edge://extensions\r\nBrave: brave://extensions\r\nVivaldi: vivaldi://extensions\r\n\r\n" +
                    "2. デベロッパーモードをONにし、「パッケージ化されていない拡張機能を読み込む」を選びます。\r\n" +
                    "「拡張フォルダーをコピー」でコピーした場所を選択します。\r\n" + installer.ExtensionDirectory + "\r\n\r\n" +
                    "3. SecretIMEの詳細でシークレット／InPrivateでの実行を許可します。\r\n4. 拡張のポップアップで連動をONにします。\r\n\r\n" +
                    "更新の場合は既存の拡張カードの再読み込みを押してください。\r\nバックアップ: " + installer.LastBackup;
            });
            install.Enabled = consent.Checked;
        }
    }
}
