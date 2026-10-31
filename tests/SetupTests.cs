using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using SecretIme.Setup;

public static class SetupTests
{
    private static int passed;
    private static string testRoot;
    private sealed class FakeRegistrations : IRegistrationStore
    {
        public readonly Dictionary<string, string> Values = new Dictionary<string, string>();
        public int Writes;
        public int FailOnceAt = -1;
        public string Read(string key) { string value; return Values.TryGetValue(key, out value) ? value : null; }
        public void Write(string key, string value)
        {
            Writes++;
            if (Writes == FailOnceAt) throw new IOException("injected registry error");
            Values[key] = value;
        }
    }
    private static MemoryStream Payload(string changed = null, string badName = null)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, true))
            foreach (string file in Installer.PayloadFiles)
            {
                string name = badName != null && file == "README.md" ? badName : file;
                using (var writer = new StreamWriter(archive.CreateEntry(name).Open(), Encoding.UTF8))
                    writer.Write(file == "README.md" && changed != null ? changed : "original:" + file);
            }
        stream.Position = 0;
        return stream;
    }
    private static string DirectoryForTest() { return Path.Combine(testRoot, Guid.NewGuid().ToString("N")); }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Reject(Action action)
    {
        try { action(); } catch (IOException) { return; } catch (InvalidDataException) { return; }
        throw new Exception("Expected an I/O rejection");
    }
    private static void Run(string name, Action action) { action(); passed++; Console.WriteLine("PASS " + name); }
    public static int Main(string[] args)
    {
        try
        {
            // All file writes are below the build output. No real registry store, GUI or host is used.
            testRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "test-runs", "setup-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(testRoot);
            Run("install copies exact payload and four fake registrations", delegate {
                var store = new FakeRegistrations(); string root = DirectoryForTest(); var setup = new Installer(root, store);
                using (var payload = Payload()) setup.Install(payload, new byte[] { 1, 2, 3 });
                Check(store.Values.Count == 4, "Missing registrations");
                Check(store.Values.Values.All(v => v == Path.Combine(root, "build", "native-host.json")), "Wrong manifest");
                Check(File.Exists(Path.Combine(root, "SecretIME.Setup.exe")), "Missing maintenance copy");
                string json = File.ReadAllText(Path.Combine(root, "build", "host-settings.json"));
                Check(json.Contains("\"automationEnabled\":true") && json.Contains(Installer.ExtensionId), "Explicit opt-in lost");
                Check(Directory.Exists(setup.LastBackup), "Missing registry backup");
            });
            Run("foreign registration rejected before creating target", delegate {
                var store = new FakeRegistrations(); store.Values[Installer.RegistrationKeys[2]] = @"C:\foreign\native.json";
                string root = DirectoryForTest(); var setup = new Installer(root, store);
                using (var payload = Payload()) Reject(delegate { setup.Install(payload, null); });
                Check(!Directory.Exists(root) && store.Writes == 0, "Foreign registration was modified");
            });
            Run("unknown nonempty directory is preserved", delegate {
                string root = DirectoryForTest(); Directory.CreateDirectory(root); File.WriteAllText(Path.Combine(root, "keep.txt"), "keep");
                var setup = new Installer(root, new FakeRegistrations());
                using (var payload = Payload()) Reject(delegate { setup.Install(payload, null); });
                Check(Directory.GetFiles(root).Length == 1, "Unowned directory changed");
            });
            Run("payload path traversal rejected before writes", delegate {
                string root = DirectoryForTest(); var setup = new Installer(root, new FakeRegistrations());
                using (var payload = Payload(null, "../outside.txt")) Reject(delegate { setup.Install(payload, null); });
                Check(!Directory.Exists(root), "Invalid payload reached file system");
            });
            Run("duplicate payload entry rejected", delegate {
                using (var payload = Payload(null, "LICENSE")) Reject(delegate { Installer.ReadPayload(payload); });
            });
            Run("update backs up prior files and retains recovery state", delegate {
                string root = DirectoryForTest(); var setup = new Installer(root, new FakeRegistrations());
                using (var payload = Payload()) setup.Install(payload, null);
                string prior = File.ReadAllText(Path.Combine(root, "README.md"));
                File.WriteAllText(Path.Combine(root, "build", "recovery-state.txt"), "clean-owned\n");
                using (var payload = Payload("updated")) setup.Install(payload, null);
                Check(File.ReadAllText(Path.Combine(setup.LastBackup, "README.md")) == prior, "Prior file not backed up");
                Check(File.ReadAllText(Path.Combine(root, "README.md")) == "updated", "Update failed");
                Check(File.ReadAllText(Path.Combine(root, "build", "recovery-state.txt")) == "clean-owned\n", "Recovery state lost");
            });
            Run("new install rolls back partial registration writes", delegate {
                string root = DirectoryForTest(); var store = new FakeRegistrations { FailOnceAt = 3 }; var setup = new Installer(root, store);
                using (var payload = Payload()) Reject(delegate { setup.Install(payload, null); });
                Check(Installer.RegistrationKeys.All(k => store.Read(k) == null), "Partial registration retained");
                Check(File.Exists(Path.Combine(setup.LastBackup, "registrations-before.json")), "Backup lost");
            });
            Run("failed update restores previous files and registrations", delegate {
                string root = DirectoryForTest(); var store = new FakeRegistrations(); var setup = new Installer(root, store);
                using (var payload = Payload()) setup.Install(payload, null);
                string prior = File.ReadAllText(Path.Combine(root, "README.md")); store.FailOnceAt = store.Writes + 2;
                using (var payload = Payload("should roll back")) Reject(delegate { setup.Install(payload, null); });
                Check(File.ReadAllText(Path.Combine(root, "README.md")) == prior, "File rollback failed");
                Check(Installer.RegistrationKeys.All(k => store.Read(k) == Path.Combine(root, "build", "native-host.json")), "Registration rollback failed");
            });
            Run("unregister keeps files and recovery journal", delegate {
                string root = DirectoryForTest(); var store = new FakeRegistrations(); var setup = new Installer(root, store);
                using (var payload = Payload()) setup.Install(payload, null);
                File.WriteAllText(Path.Combine(root, "build", "recovery-state.txt"), "dirty\n");
                setup.Unregister();
                Check(Installer.RegistrationKeys.All(k => store.Read(k) == null), "Registration not removed");
                Check(File.Exists(Path.Combine(root, "build", "SecretIME.Host.exe")), "Host file removed");
                Check(File.ReadAllText(Path.Combine(root, "build", "recovery-state.txt")) == "dirty\n", "Journal removed");
            });
            Run("unregister refuses changed foreign registration", delegate {
                string root = DirectoryForTest(); var store = new FakeRegistrations(); var setup = new Installer(root, store);
                using (var payload = Payload()) setup.Install(payload, null);
                store.Values[Installer.RegistrationKeys[1]] = @"C:\foreign\manifest.json"; int writes = store.Writes;
                Reject(setup.Unregister);
                Check(store.Writes == writes, "Foreign registration removed");
            });
            Run("failed unregister restores earlier removals", delegate {
                string root = DirectoryForTest(); var store = new FakeRegistrations(); var setup = new Installer(root, store);
                using (var payload = Payload()) setup.Install(payload, null);
                store.FailOnceAt = store.Writes + 3;
                Reject(setup.Unregister);
                Check(Installer.RegistrationKeys.All(k => store.Read(k) == Path.Combine(root, "build", "native-host.json")), "Unregister rollback failed");
            });
            if (args.Length == 1)
                Run("compiled setup embeds validated production payload", delegate {
                    var assembly = System.Reflection.Assembly.LoadFile(Path.GetFullPath(args[0]));
                    using (Stream payload = assembly.GetManifestResourceStream("SecretIME.Payload"))
                        Check(Installer.ReadPayload(payload).Count == Installer.PayloadFiles.Length, "Embedded payload mismatch");
                });
            Console.WriteLine(passed + " setup tests passed. Artifacts retained: " + testRoot);
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
