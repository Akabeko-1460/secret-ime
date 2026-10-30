using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using SecretIme;

public static class CoreTests
{
    private static int passed;
    private sealed class FakeAdapter : IPrivacyAdapter
    {
        public bool? Mode = false;
        public int Reads, Enables, Restores;
        public bool EnableResult = true, RestoreResult = true, ThrowRead, ThrowWrite;
        public bool? AfterEnable = true;
        public bool? Read() { Reads++; if (ThrowRead) throw new InvalidOperationException(); return Mode; }
        public bool Enable()
        {
            Enables++;
            if (ThrowWrite) throw new InvalidOperationException();
            Mode = AfterEnable; return EnableResult;
        }
        public bool Restore()
        {
            Restores++;
            if (ThrowWrite) throw new InvalidOperationException();
            if (RestoreResult) Mode = false;
            return RestoreResult;
        }
    }
    private sealed class FragmentedStream : MemoryStream
    {
        public FragmentedStream(byte[] bytes) : base(bytes) { }
        public override int Read(byte[] buffer, int offset, int count) { return base.Read(buffer, offset, Math.Min(1, count)); }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Run(string name, Action test) { test(); passed++; Console.WriteLine("PASS " + name); }
    private static void Reject(Action operation)
    {
        try { operation(); } catch (InvalidDataException) { return; }
        throw new Exception("Expected InvalidDataException");
    }
    private static NativeRequest Parse(string json) { return NativeProtocol.ParseRequest(Encoding.UTF8.GetBytes(json)); }

    public static int Main()
    {
        try
        {
            Run("multiple browsers retain mode until explicit final restore", delegate {
                var adapter = new FakeAdapter(); var core = new LeaseController(adapter, true);
                Check(core.Sync("chrome", true).State == "mode-enabled", "Enable not confirmed");
                core.Sync("brave", true); core.Sync("chrome", true);
                Check(core.Disconnect("chrome").ActiveClients == 1, "One browser removed another lease");
                Check(adapter.Restores == 0 && adapter.Enables == 1, "Unexpected write");
                Check(core.Disconnect("brave").State == "recovery-needed", "Final disconnect must request review");
                Check(adapter.Restores == 0 && adapter.Mode == true, "Automatic restore occurred");
                Check(core.Restore().State == "idle" && adapter.Restores == 1, "Explicit restore failed");
            });
            Run("pre-existing ON never becomes an owned OFF restoration", delegate {
                var adapter = new FakeAdapter { Mode = true }; var core = new LeaseController(adapter, true);
                core.Sync("a", true); core.Disconnect("a"); core.Restore();
                Check(adapter.Enables == 0 && adapter.Restores == 0 && adapter.Mode == true, "Pre-existing ON changed");
            });
            Run("restore blocked with active clients", delegate {
                var adapter = new FakeAdapter(); var core = new LeaseController(adapter, true);
                core.Sync("a", true); core.Restore();
                Check(adapter.Restores == 0 && core.Status().ActiveClients == 1, "Restored during active lease");
            });
            Run("manual OFF respected without re-enable", delegate {
                var adapter = new FakeAdapter(); var core = new LeaseController(adapter, true);
                core.Sync("a", true); adapter.Mode = false;
                Check(core.Sync("a", true).State == "recovery-needed", "Manual change not detected");
                core.Sync("b", true); core.Disconnect("a"); core.Disconnect("b"); core.Restore();
                Check(adapter.Enables == 1 && adapter.Restores == 0, "Manual choice overwritten");
            });
            Run("manual OFF of pre-existing ON respected", delegate {
                var adapter = new FakeAdapter { Mode = true }; var core = new LeaseController(adapter, true);
                core.Sync("a", true); adapter.Mode = false;
                Check(core.Sync("a", true).State == "recovery-needed" && adapter.Enables == 0, "Manual pre-existing ON change overwritten");
            });
            Run("unknown read causes no writes", delegate {
                var adapter = new FakeAdapter { Mode = null }; var core = new LeaseController(adapter, true);
                Check(core.Sync("a", true).State == "unavailable", "Unknown state misrepresented");
                core.Disconnect("a"); core.Restore();
                Check(adapter.Enables == 0 && adapter.Restores == 0, "Write after unknown state");
            });
            Run("unknown after ownership invalidates restoration", delegate {
                var adapter = new FakeAdapter(); var core = new LeaseController(adapter, true);
                core.Sync("a", true); adapter.Mode = null; core.Status();
                adapter.Mode = true; core.Disconnect("a"); core.Restore();
                Check(adapter.Restores == 0 && core.Status().State == "recovery-needed", "Lost ownership recovered unsafely");
            });
            Run("failed enable cannot claim protection", delegate {
                var adapter = new FakeAdapter { EnableResult = false, AfterEnable = true };
                var core = new LeaseController(adapter, true);
                Check(core.Sync("a", true).State == "recovery-needed", "Unconfirmed enable claimed success");
                core.Disconnect("a"); core.Restore(); Check(adapter.Restores == 0, "Unowned partial change restored");
            });
            Run("failed restore never retries automatically", delegate {
                var adapter = new FakeAdapter { RestoreResult = false }; var core = new LeaseController(adapter, true);
                core.Sync("a", true); core.Disconnect("a");
                Check(core.Restore().State == "recovery-needed", "Restore failure hidden");
                core.Restore(); core.Status(); core.Sync("b", true);
                Check(adapter.Restores == 1 && adapter.Enables == 1, "Retried unsafe write");
            });
            Run("restart dirty marker cannot restore unowned mode", delegate {
                var adapter = new FakeAdapter { Mode = true }; var core = new LeaseController(adapter, true, true);
                Check(core.Sync("a", true).State == "recovery-needed", "Crash recovery hidden");
                core.Disconnect("a"); core.Restore();
                Check(adapter.Enables == 0 && adapter.Restores == 0, "Crash cleanup forced mode");
                adapter.Mode = false;
                Check(core.Restore().State == "idle", "Manual recovery acknowledgment failed");
            });
            Run("clean broker handoff preserves explicit restore without automatic writes", delegate {
                var adapter = new FakeAdapter(); var first = new LeaseController(adapter, true);
                first.Sync("a", true); first.Disconnect("a");
                Check(first.HasOwnedChange && first.Status().ModeEnabled == true, "Clean ownership not available");
                var next = new LeaseController(adapter, true, true, first.HasOwnedChange);
                Check(next.Status().State == "recovery-needed" && next.HasOwnedChange, "Clean recovery ownership lost");
                Check(next.Sync("b", true).State == "recovery-needed", "Clean handoff bypassed recovery");
                next.Restore();
                Check(adapter.Enables == 1 && adapter.Restores == 0, "Handoff changed mode automatically or while active");
                next.Disconnect("b");
                Check(next.Restore().State == "idle" && adapter.Restores == 1 && !next.HasOwnedChange,
                    "Explicit restore after clean handoff failed");
            });
            Run("clean handoff loses ownership after unknown OFF or restore failure", delegate {
                foreach (bool? observed in new bool?[] { null, false })
                {
                    var adapter = new FakeAdapter { Mode = observed };
                    var core = new LeaseController(adapter, true, true, true);
                    core.Status();
                    Check(!core.HasOwnedChange, "Unknown or manual OFF retained ownership");
                    adapter.Mode = true; core.Restore();
                    Check(adapter.Restores == 0 && core.Status().State == "recovery-needed", "Lost ownership restored OFF");
                }
                var failing = new FakeAdapter { Mode = true, RestoreResult = false };
                var restored = new LeaseController(failing, true, true, true);
                restored.Restore(); restored.Restore();
                Check(!restored.HasOwnedChange && failing.Restores == 1, "Failed handoff restore retried");
                var invalid = new LeaseController(new FakeAdapter { Mode = true }, true, false, true);
                Check(!invalid.HasOwnedChange, "Unmarked startup acquired ownership");
            });
            Run("adapter exceptions are unavailable without writes", delegate {
                var adapter = new FakeAdapter { ThrowRead = true }; var core = new LeaseController(adapter, true);
                Check(core.Sync("a", true).State == "unavailable", "Read exception escaped");
                adapter.ThrowRead = false; adapter.ThrowWrite = true;
                Check(core.Sync("a", true).State == "recovery-needed", "Write exception claimed success");
            });
            Run("unconfigured adapter never queried", delegate {
                var adapter = new FakeAdapter(); var core = new LeaseController(adapter, false);
                Check(core.Sync("a", true).State == "not-configured", "Missing configuration hidden");
                core.Restore(); core.Status();
                Check(adapter.Reads == 0 && adapter.Enables == 0 && adapter.Restores == 0, "Unconfigured adapter used");
            });
            Run("parallel clients share one enable and no automatic restore", delegate {
                var adapter = new FakeAdapter(); var core = new LeaseController(adapter, true);
                var threads = new List<Thread>();
                for (int index = 0; index < 20; index++)
                {
                    string id = index.ToString();
                    var thread = new Thread(delegate() { core.Sync(id, true); }); threads.Add(thread); thread.Start();
                }
                foreach (Thread thread in threads) thread.Join();
                Check(core.Status().ActiveClients == 20 && adapter.Enables == 1, "Concurrent lease mismatch");
                for (int index = 0; index < 20; index++) core.Disconnect(index.ToString());
                Check(adapter.Restores == 0 && core.Status().RestoreRequired, "Concurrent release restored automatically");
            });
            Run("strict valid protocol and escaped member names", delegate {
                Check(Parse("{\"v\":1,\"op\":\"sync\",\"active\":true}").Active, "sync parsing");
                Check(Parse(" { \"v\":1, \"op\":\"status\" }\r\n").Operation == "status", "status parsing");
                Check(Parse("{\"\\u0076\":1,\"op\":\"restore\"}").Operation == "restore", "escaped key parsing");
            });
            Run("strict malformed schema rejection", delegate {
                string[] invalid = {
                    "[]", "null", "{}", "{\"v\":1,\"op\":\"unknown\"}",
                    "{\"v\":1,\"op\":\"sync\"}", "{\"v\":1,\"op\":\"sync\",\"active\":1}",
                    "{\"v\":1,\"op\":\"status\",\"active\":false}", "{\"v\":1,\"op\":\"status\",\"url\":\"x\"}",
                    "{\"v\":true,\"op\":\"status\"}", "{\"v\":1.0,\"op\":\"status\"}",
                    "{\"v\":01,\"op\":\"status\"}", "{\"v\":1,\"op\":\"status\",}",
                    "{\"v\":1,\"v\":1,\"op\":\"status\"}", "{\"v\":1,\"\\u0076\":1,\"op\":\"status\"}",
                    "{'v':1,'op':'status'}", "{\"v\":1,\"op\":\"stat\\x75s\"}",
                    "{\"v\":1,\"op\":\"status\"}{}", "{\"v\":1,\"op\":{}}", "{\"v\":1,\"op\":\"status\"/*x*/}"
                };
                foreach (string json in invalid) Reject(delegate { Parse(json); });
                Reject(delegate { NativeProtocol.ParseRequest(new byte[] { 0xff }); });
            });
            Run("fragmented framing supports multiple frames and clean EOF", delegate {
                var output = new MemoryStream();
                NativeProtocol.WritePayload(output, Encoding.UTF8.GetBytes("{\"v\":1,\"op\":\"sync\",\"active\":false}"));
                NativeProtocol.WritePayload(output, Encoding.UTF8.GetBytes("{\"v\":1,\"op\":\"status\"}"));
                var input = new FragmentedStream(output.ToArray());
                Check(!NativeProtocol.ReadRequest(input).Active, "Fragmented payload failed");
                Check(NativeProtocol.ReadRequest(input).Operation == "status", "Second frame failed");
                Check(NativeProtocol.ReadRequest(input) == null, "Clean EOF not accepted");
            });
            Run("oversized zero partial header and partial body rejected", delegate {
                foreach (byte[] bytes in new [] {
                    new byte[] { 0, 0, 0, 0 }, new byte[] { 1, 8, 0, 0 }, new byte[] { 0, 0, 16, 0 },
                    new byte[] { 0xff, 0xff, 0xff, 0xff }, new byte[] { 1 }, new byte[] { 2, 0, 0, 0, 1 }
                }) Reject(delegate { NativeProtocol.ReadRequest(new MemoryStream(bytes)); });
                Reject(delegate { NativeProtocol.WritePayload(new MemoryStream(), new byte[2049]); });
            });
            Run("status wire fields never claim effectiveness", delegate {
                var status = new LeaseController(new FakeAdapter(), true).Status(); var output = new MemoryStream();
                NativeProtocol.WriteStatus(output, status);
                string json = Encoding.UTF8.GetString(NativeProtocol.ReadPayload(new MemoryStream(output.ToArray())));
                Check(json.Contains("\"effectiveness\":\"unverified\"") && json.Contains("\"restoreRequired\":false") &&
                    json.Contains("\"modeEnabled\":false") && json.Contains("\"detail\":"), "Wire contract mismatch");
            });
            Console.WriteLine("PASS " + passed + " core/protocol tests; no real IME test performed"); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine("FAIL " + error.Message); return 1; }
    }
}
