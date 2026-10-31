using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace SecretIme.Setup
{
    public interface IRegistrationStore
    {
        string Read(string key);
        void Write(string key, string value);
    }

    // Only the UI entry point constructs this real HKCU implementation.
    public sealed class CurrentUserRegistrations : IRegistrationStore
    {
        public string Read(string path)
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(path, false))
            {
                if (key == null) return null;
                if (!key.GetValueNames().Contains(""))
                {
                    if (key.ValueCount != 0 || key.SubKeyCount != 0)
                        throw new IOException("既存の登録形式を認識できません。変更せず終了します。");
                    return null;
                }
                if (key.GetValueKind("") != RegistryValueKind.String)
                    throw new IOException("既存の登録形式を認識できません。変更せず終了します。");
                return (string)key.GetValue("", null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            }
        }

        public void Write(string path, string value)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(path))
            {
                if (value == null) key.DeleteValue("", false);
                else key.SetValue("", value, RegistryValueKind.String);
            }
        }
    }

    public sealed class Installer
    {
        private const string Ownership = "SecretIME per-user setup v1\n";
        public const string ExtensionId = "djbbbndacfniipaiopkjbeipamnjaneo";
        public static readonly string[] RegistrationKeys = {
            @"SOFTWARE\Google\Chrome\NativeMessagingHosts\jp.secretime.host",
            @"SOFTWARE\Microsoft\Edge\NativeMessagingHosts\jp.secretime.host",
            @"SOFTWARE\BraveSoftware\Brave-Browser\NativeMessagingHosts\jp.secretime.host",
            @"SOFTWARE\Vivaldi\NativeMessagingHosts\jp.secretime.host"
        };
        public static readonly string[] PayloadFiles = {
            "README.md", "LICENSE", "docs/install.md", "build/SecretIME.Host.exe",
            "extension/manifest.json", "extension/background.mjs", "extension/controller.mjs",
            "extension/presentation.mjs", "extension/popup.mjs", "extension/popup.html",
            "extension/popup.css", "extension/README.md"
        };
        private readonly string root;
        private readonly IRegistrationStore registrations;
        public string LastBackup { get; private set; }
        public string ExtensionDirectory { get { return Path.Combine(root, "extension"); } }
        private string Manifest { get { return Path.Combine(root, "build", "native-host.json"); } }

        public Installer(string directory, IRegistrationStore store)
        {
            root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
            if (Path.GetPathRoot(root).TrimEnd(Path.DirectorySeparatorChar) == root)
                throw new IOException("ドライブ直下へは導入できません。");
            registrations = store;
        }

        private string Target(string relative)
        {
            string path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException("導入先の範囲外へは書き込めません。");
            AssertOrdinaryPath(path);
            return path;
        }

        // Check ancestors too: a junction above the install directory must not redirect writes.
        private static void AssertOrdinaryPath(string path)
        {
            for (string current = path; current != null; current = Path.GetDirectoryName(current))
                if ((File.Exists(current) || Directory.Exists(current)) &&
                    (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("リンクされた導入先は使えません。");
        }

        private void CheckOwnership()
        {
            AssertOrdinaryPath(root);
            string marker = Target("setup-owner.txt");
            if (File.Exists(marker))
            {
                if (new FileInfo(marker).Length > 100 || File.ReadAllText(marker) != Ownership)
                    throw new IOException("導入先に別のファイルがあります。変更せず終了します。");
            }
            else if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
                throw new IOException("導入先はSecretIMEが作成したフォルダーではありません。");
        }

        private Dictionary<string, string> ReadOwnedRegistrations()
        {
            var snapshot = new Dictionary<string, string>();
            foreach (string key in RegistrationKeys)
            {
                string value = registrations.Read(key);
                if (value != null && !String.Equals(value, Manifest, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("別のSecretIMEが登録されています。以前の版で登録解除してからやり直してください。");
                snapshot.Add(key, value);
            }
            return snapshot;
        }

        public static Dictionary<string, byte[]> ReadPayload(Stream stream)
        {
            var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Read, true))
            {
                if (archive.Entries.Count != PayloadFiles.Length) throw new InvalidDataException("配布ファイル数が不正です。");
                long total = 0;
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    if (!PayloadFiles.Contains(entry.FullName) || files.ContainsKey(entry.FullName) ||
                        entry.Length > 4 * 1024 * 1024 || (total += entry.Length) > 8 * 1024 * 1024)
                        throw new InvalidDataException("配布ファイルを確認できません。");
                    using (Stream input = entry.Open())
                    using (var memory = new MemoryStream())
                    {
                        byte[] buffer = new byte[8192];
                        int count;
                        while ((count = input.Read(buffer, 0, buffer.Length)) != 0)
                        {
                            if (memory.Length + count > entry.Length) throw new InvalidDataException("配布ファイルが破損しています。");
                            memory.Write(buffer, 0, count);
                        }
                        if (memory.Length != entry.Length) throw new InvalidDataException("配布ファイルが破損しています。");
                        files.Add(entry.FullName, memory.ToArray());
                    }
                }
            }
            return files;
        }

        private static byte[] Json(object value)
        {
            return Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(value) + "\n");
        }

        private string CreateBackup(Dictionary<string, string> snapshot)
        {
            string backup = Target("backups/" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(backup);
            File.WriteAllBytes(Path.Combine(backup, "registrations-before.json"), Json(snapshot));
            LastBackup = backup;
            return backup;
        }

        private void WriteFile(string relative, byte[] bytes)
        {
            string destination = Target(relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            string pending = Target(relative + "." + Guid.NewGuid().ToString("N") + ".pending");
            using (var output = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                output.Write(bytes, 0, bytes.Length);
                output.Flush(true);
            }
            if (File.Exists(destination)) File.Replace(pending, destination, null);
            else File.Move(pending, destination);
        }

        public void Install(Stream payload, byte[] setupExecutable)
        {
            Dictionary<string, byte[]> files = ReadPayload(payload);
            CheckOwnership();
            Dictionary<string, string> snapshot = ReadOwnedRegistrations();
            string origin = "chrome-extension://" + ExtensionId + "/";
            files.Add("build/native-host.json", Json(new {
                name = "jp.secretime.host", description = "SecretIME local IME mode assistant",
                path = Target("build/SecretIME.Host.exe"), type = "stdio", allowed_origins = new[] { origin }
            }));
            files.Add("build/host-settings.json", Json(new { automationEnabled = true, allowedOrigins = new[] { origin } }));
            // The maintenance copy provides a GUI registration removal without PowerShell.
            if (setupExecutable != null) files.Add("SecretIME.Setup.exe", setupExecutable);
            foreach (string relative in files.Keys)
            {
                string target = Target(relative);
                if (Directory.Exists(target)) throw new IOException("導入先に同名のフォルダーがあります。");
            }
            string backup = CreateBackup(snapshot);
            var originalFiles = new Dictionary<string, byte[]>();
            foreach (string relative in files.Keys)
                if (File.Exists(Target(relative)))
                {
                    byte[] original = File.ReadAllBytes(Target(relative));
                    originalFiles.Add(relative, original);
                    string saved = Path.Combine(backup, relative.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(saved));
                    File.WriteAllBytes(saved, original);
                }
            if (!File.Exists(Target("setup-owner.txt"))) WriteFile("setup-owner.txt", Encoding.UTF8.GetBytes(Ownership));
            var writtenFiles = new List<string>();
            var writtenKeys = new List<string>();
            try
            {
                foreach (var file in files)
                {
                    WriteFile(file.Key, file.Value);
                    writtenFiles.Add(file.Key);
                }
                foreach (string key in RegistrationKeys)
                {
                    if (registrations.Read(key) != snapshot[key]) throw new IOException("登録が途中で変更されたため停止しました。");
                    // Include the key before the call: a write may succeed and then report an error.
                    writtenKeys.Add(key);
                    registrations.Write(key, Manifest);
                }
            }
            catch (Exception error)
            {
                bool restored = true;
                foreach (string key in writtenKeys)
                    try
                    {
                        if (registrations.Read(key) == Manifest) registrations.Write(key, snapshot[key]);
                        else if (registrations.Read(key) != snapshot[key]) restored = false;
                    }
                    catch { restored = false; }
                foreach (string relative in writtenFiles)
                    try
                    {
                        byte[] original;
                        if (originalFiles.TryGetValue(relative, out original)) WriteFile(relative, original);
                    }
                    catch { restored = false; }
                throw new IOException((restored ? "導入に失敗しました。以前のファイルと登録は復旧しました。新規ファイルは保持しています。" :
                    "導入に失敗し、一部を復旧できませんでした。再試行せずバックアップを確認してください。") +
                    "\nバックアップ: " + backup, error);
            }
        }

        public void Unregister()
        {
            CheckOwnership();
            Dictionary<string, string> snapshot = ReadOwnedRegistrations();
            CreateBackup(snapshot);
            var changed = new List<string>();
            try
            {
                foreach (string key in RegistrationKeys)
                {
                    if (registrations.Read(key) != snapshot[key]) throw new IOException("登録が変更されたため停止しました。");
                    if (snapshot[key] == null) continue;
                    changed.Add(key);
                    registrations.Write(key, null);
                }
            }
            catch (Exception error)
            {
                bool restored = true;
                foreach (string key in changed)
                    try
                    {
                        if (registrations.Read(key) == null) registrations.Write(key, snapshot[key]);
                        else if (registrations.Read(key) != snapshot[key]) restored = false;
                    }
                    catch { restored = false; }
                throw new IOException((restored ? "登録解除に失敗しました。以前の登録は復旧しました。" :
                    "登録解除に失敗し、一部を復旧できませんでした。") + "\nバックアップ: " + LastBackup, error);
            }
            // Retain files and the recovery journal. Never change the IME during setup/removal.
        }
    }
}
