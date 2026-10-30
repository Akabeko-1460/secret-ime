using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace SecretIme
{
    internal static class LocalPeer
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetNamedPipeServerProcessId(IntPtr pipe, out uint processId);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetNamedPipeClientProcessId(IntPtr pipe, out uint processId);

        internal static bool IsOurExecutable(PipeStream pipe, bool server)
        {
            try
            {
                uint processId;
                bool found = server ? GetNamedPipeServerProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out processId) :
                    GetNamedPipeClientProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out processId);
                if (!found) return false;
                using (Process peer = Process.GetProcessById((int)processId))
                using (Process current = Process.GetCurrentProcess())
                    return peer.SessionId == current.SessionId && String.Equals(peer.MainModule.FileName,
                        current.MainModule.FileName, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception) { return false; }
        }
    }

    public sealed class HostSettings
    {
        public bool AutomationEnabled { get; private set; }
        public HashSet<string> AllowedOrigins { get; private set; }
        public static readonly string DirectoryPath = AppDomain.CurrentDomain.BaseDirectory;
        public const string DefaultOrigin = "chrome-extension://djbbbndacfniipaiopkjbeipamnjaneo/";

        public static HostSettings Load()
        {
            var settings = new HostSettings {
                AllowedOrigins = new HashSet<string>(StringComparer.Ordinal) { DefaultOrigin }
            };
            string path = Path.Combine(DirectoryPath, "host-settings.json");
            if (!File.Exists(path)) return settings;
            if (new FileInfo(path).Length > 4096) throw new InvalidDataException("Invalid settings");
            var values = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(path));
            object enabled, origins;
            if (values == null || values.Count != 2 ||
                !values.TryGetValue("automationEnabled", out enabled) || !(enabled is bool) ||
                !values.TryGetValue("allowedOrigins", out origins) || !(origins is System.Collections.IList))
                throw new InvalidDataException("Invalid settings");
            settings.AllowedOrigins.Clear();
            foreach (object value in (System.Collections.IList)origins)
            {
                string origin = value as string;
                if (!ValidOrigin(origin)) throw new InvalidDataException("Invalid allowed origin");
                settings.AllowedOrigins.Add(origin);
            }
            if (settings.AllowedOrigins.Count == 0 || settings.AllowedOrigins.Count > 8)
                throw new InvalidDataException("Invalid origin count");
            settings.AutomationEnabled = (bool)enabled;
            return settings;
        }

        public static bool ValidOrigin(string origin)
        {
            const string prefix = "chrome-extension://";
            if (origin == null || !origin.StartsWith(prefix, StringComparison.Ordinal) ||
                origin.Length != prefix.Length + 33 || !origin.EndsWith("/", StringComparison.Ordinal)) return false;
            for (int i = prefix.Length; i < origin.Length - 1; i++)
                if (origin[i] < 'a' || origin[i] > 'p') return false;
            return true;
        }
    }

    public sealed class RecoveryJournal
    {
        private readonly string path;
        public RecoveryJournal(string directory) { path = Path.Combine(directory, "recovery-state.txt"); }
        public string Read()
        {
            if (!File.Exists(path)) return "none";
            if (new FileInfo(path).Length > 64) return "dirty";
            string value = File.ReadAllText(path).Trim();
            return value == "none" || value == "owned" ? value : "dirty";
        }
        public void Write(string value)
        {
            if (value != "none" && value != "owned" && value != "dirty") throw new ArgumentException("Invalid state");
            string unique = Guid.NewGuid().ToString("N");
            string pending = path + ".pending-" + unique;
            using (var stream = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                byte[] bytes = Encoding.ASCII.GetBytes(value + "\n");
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }
            if (File.Exists(path))
            {
                string backups = Path.Combine(Path.GetDirectoryName(path), "state-backups");
                Directory.CreateDirectory(backups);
                File.Replace(pending, path, Path.Combine(backups, unique + ".txt"));
            }
            else File.Move(pending, path);
        }
    }

    // UI Automation providers can hang. Isolate them in our own bounded worker.
    public sealed class IsolatedUiAdapter : IPrivacyAdapter, IDisposable
    {
        private Process worker;
        private bool failed;
        public bool? Read() { return Exchange("status").ModeEnabled; }
        public bool Enable() { CoreStatus status = Exchange("sync"); return status.ModeEnabled == true && status.State != "unavailable"; }
        public bool Restore() { CoreStatus status = Exchange("restore"); return status.ModeEnabled == false && status.State != "unavailable"; }

        private CoreStatus Exchange(string operation)
        {
            if (failed) return Failure();
            try
            {
                if (worker == null)
                    worker = Process.Start(new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName, "--ui-worker") {
                        UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                        RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                        WorkingDirectory = HostSettings.DirectoryPath
                    });
                if (worker == null || worker.HasExited) throw new IOException("Worker unavailable");
                var request = new Dictionary<string, object> { { "v", 1 }, { "op", operation } };
                if (operation == "sync") request.Add("active", true);
                NativeProtocol.WritePayload(worker.StandardInput.BaseStream,
                    Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(request)));
                byte[] reply = ReadBounded(worker.StandardOutput.BaseStream, 12000);
                if (reply == null) throw new IOException("Worker ended");
                var values = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(reply));
                object mode = values["modeEnabled"];
                return new CoreStatus { State = (string)values["state"], ModeEnabled = mode == null ? (bool?)null : (bool)mode };
            }
            catch (Exception) { failed = true; StopWorker(); return Failure(); }
        }

        private static CoreStatus Failure() { return new CoreStatus { State = "unavailable", ModeEnabled = null }; }
        private void StopWorker()
        {
            if (worker == null) return;
            try
            {
                worker.StandardInput.Close();
                if (!worker.WaitForExit(1000)) worker.Kill(); // only our newly spawned helper, never an IME/browser
                worker.WaitForExit(1000);
            }
            catch (InvalidOperationException) { }
            finally { worker.Dispose(); worker = null; }
        }
        public void Dispose() { StopWorker(); }
        public void EndOperation() { StopWorker(); }

        internal static byte[] ReadBounded(Stream stream, int timeoutMs)
        {
            byte[] payload = null;
            Exception failure = null;
            var read = new Thread(delegate() {
                try { payload = NativeProtocol.ReadPayload(stream); }
                catch (Exception error) { failure = error; }
            }) { IsBackground = true };
            read.Start();
            if (!read.Join(timeoutMs)) { stream.Dispose(); throw new IOException("Response timeout"); }
            if (failure != null) throw new IOException("Invalid response", failure);
            return payload;
        }
    }

    public sealed class Broker : IDisposable
    {
        private readonly object gate = new object();
        private readonly LeaseController controller;
        private readonly RecoveryJournal journal;
        private readonly string pipeName;
        private readonly HostSettings settings;
        private readonly Action operationComplete;
        private readonly ManualResetEvent stop = new ManualResetEvent(false);
        private int connections;
        private Timer idleStop;

        public Broker(LeaseController controller, RecoveryJournal journal, string pipeName, HostSettings settings,
            Action operationComplete = null)
        {
            this.controller = controller; this.journal = journal;
            this.pipeName = pipeName; this.settings = settings;
            this.operationComplete = operationComplete ?? delegate() { };
        }

        public void Run()
        {
            bool everConnected = false;
            var workers = new List<Thread>();
            while (!stop.WaitOne(0))
            {
                var security = new PipeSecurity();
                security.SetAccessRuleProtection(true, false);
                security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User, PipeAccessRights.FullControl, AccessControlType.Allow));
                using (var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096, security))
                {
                    IAsyncResult accept = pipe.BeginWaitForConnection(null, null);
                    int ready = WaitHandle.WaitAny(new[] { accept.AsyncWaitHandle, stop }, everConnected ? Timeout.Infinite : 15000);
                    if (ready != 0) break;
                    pipe.EndWaitForConnection(accept);
                    lock (gate)
                    {
                        if (stop.WaitOne(0)) break;
                        if (idleStop != null) { idleStop.Dispose(); idleStop = null; }
                        connections++;
                    }
                    everConnected = true;
                    // Run a client outside this scope while the next listener is accepting.
                    var done = new ManualResetEvent(false);
                    var client = new Thread(delegate() {
                        try { HandleClient(pipe); }
                        finally { done.Set(); }
                    }) { IsBackground = true };
                    workers.Add(client);
                    client.Start();
                    // A separate accept loop owns subsequent streams. This initial scope
                    // keeps its first stream alive until both it and all leases end.
                    AcceptMore();
                    done.WaitOne();
                    done.Dispose();
                }
            }
            foreach (Thread worker in workers) worker.Join(1000);
            lock (gate)
            {
                if (connections == 0)
                    journal.Write(controller.HasOwnedChange ? "owned" : journal.Read() == "none" ? "none" : "dirty");
            }
        }

        private void AcceptMore()
        {
            // One long-lived accept loop; connected clients run independently.
            while (!stop.WaitOne(0))
            {
                var security = new PipeSecurity();
                security.SetAccessRuleProtection(true, false);
                security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User, PipeAccessRights.FullControl, AccessControlType.Allow));
                var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096, security);
                IAsyncResult accept = pipe.BeginWaitForConnection(null, null);
                int ready = WaitHandle.WaitAny(new[] { accept.AsyncWaitHandle, stop });
                if (ready != 0) { pipe.Dispose(); break; }
                pipe.EndWaitForConnection(accept);
                lock (gate)
                {
                    if (stop.WaitOne(0)) { pipe.Dispose(); break; }
                    if (connections >= 64) { pipe.Dispose(); continue; }
                    if (idleStop != null) { idleStop.Dispose(); idleStop = null; }
                    connections++;
                }
                var client = new Thread(delegate() { using (pipe) HandleClient(pipe); }) { IsBackground = true };
                client.Start();
            }
        }

        private void HandleClient(NamedPipeServerStream pipe)
        {
            string clientId = Guid.NewGuid().ToString("N");
            try
            {
                if (!LocalPeer.IsOurExecutable(pipe, false)) return;
                byte[] handshake = IsolatedUiAdapter.ReadBounded(pipe, 3000);
                if (handshake == null || !settings.AllowedOrigins.Contains(Encoding.UTF8.GetString(handshake))) return;
                while (true)
                {
                    NativeRequest request = NativeProtocol.ReadRequest(pipe);
                    if (request == null) break;
                    CoreStatus response;
                    lock (gate)
                    {
                        try
                        {
                            response = request.Operation == "sync" ? controller.Sync(clientId, request.Active) :
                                request.Operation == "restore" ? controller.Restore() : controller.Status();
                            SaveAfterOperation(response);
                        }
                        finally { operationComplete(); }
                    }
                    NativeProtocol.WriteStatus(pipe, response);
                }
            }
            catch (Exception) { /* Never log browser requests or change OFF in cleanup. */ }
            finally
            {
                lock (gate)
                {
                    try
                    {
                        CoreStatus final = controller.Disconnect(clientId);
                        SaveAfterOperation(final);
                    }
                    catch (Exception) { /* Preserve the dirty marker if disk/ACL state changed. */ }
                    finally
                    {
                        try { operationComplete(); }
                        catch (Exception) { /* Worker cleanup cannot skip lease cleanup. */ }
                        connections--;
                        if (connections == 0)
                        {
                            idleStop = new Timer(delegate(object unused) {
                                lock (gate) if (connections == 0) stop.Set();
                            }, null, 250, Timeout.Infinite);
                        }
                    }
                }
            }
        }

        private void SaveAfterOperation(CoreStatus status)
        {
            // Clear only after a known OFF result or a confirmed explicit restore.
            // A live owned change remains dirty until orderly broker shutdown.
            if (controller.HasOwnedChange) return;
            if (status.ModeEnabled == false && status.State != "recovery-needed" && journal.Read() != "none") journal.Write("none");
        }

        public void Dispose()
        {
            lock (gate) { if (idleStop != null) idleStop.Dispose(); stop.Set(); }
            stop.Dispose();
        }
    }

    public static class Program
    {
        internal static string PipeName()
        {
            return "SecretIME.v1." + WindowsIdentity.GetCurrent().User.Value;
        }

        [STAThread]
        public static int Main(string[] args)
        {
            try
            {
                HostSettings settings = HostSettings.Load();
                if (args.Length == 1 && args[0] == "--diagnose")
                {
                    using (var adapter = new IsolatedUiAdapter())
                    {
                        bool? mode = adapter.Read();
                        Console.WriteLine(new JavaScriptSerializer().Serialize(new {
                            toolTrusted = GoogleUiAdapter.TrustedToolPath() != null,
                            automationEnabled = settings.AutomationEnabled,
                            modeEnabled = mode, effectiveness = "unverified"
                        }));
                    }
                    return 0;
                }
                if (args.Length == 1 && args[0] == "--ui-worker") return RunUiWorker(settings);
                if (args.Length == 1 && args[0] == "--broker") return RunBroker(settings);
                if (args.Length < 1 || args.Length > 2 || !settings.AllowedOrigins.Contains(args[0])) return 2;
                if (args.Length == 2 && !System.Text.RegularExpressions.Regex.IsMatch(args[1], @"^--parent-window=\d+$")) return 2;
                return RunNative(args[0]);
            }
            catch (Exception)
            {
                // stdout is reserved exclusively for framed native responses.
                return 2;
            }
        }

        private static int RunUiWorker(HostSettings settings)
        {
            var journal = new RecoveryJournal(HostSettings.DirectoryPath);
            var adapter = new GoogleUiAdapter(delegate() {
                if (!settings.AutomationEnabled) throw new InvalidOperationException("Automation disabled");
                journal.Write("dirty");
            });
            using (Stream input = Console.OpenStandardInput())
            using (Stream output = Console.OpenStandardOutput())
            {
                NativeRequest request;
                while ((request = NativeProtocol.ReadRequest(input)) != null)
                {
                    bool success = true;
                    bool? mode;
                    if (request.Operation == "status") mode = adapter.Read();
                    else
                    {
                        success = settings.AutomationEnabled && (request.Operation == "restore" ? adapter.Restore() : adapter.Enable());
                        mode = success ? (bool?)(request.Operation != "restore") : null;
                    }
                    NativeProtocol.WriteStatus(output, new CoreStatus {
                        State = success && mode.HasValue ? "idle" : "unavailable",
                        Detail = adapter.LastDetail, ModeEnabled = mode
                    });
                }
            }
            return 0;
        }

        private static int RunBroker(HostSettings settings)
        {
            bool created;
            var security = new MutexSecurity();
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new MutexAccessRule(WindowsIdentity.GetCurrent().User, MutexRights.FullControl, AccessControlType.Allow));
            using (var mutex = new Mutex(false, @"Global\" + PipeName(), out created, security))
            {
                bool acquired;
                try { acquired = mutex.WaitOne(0); }
                catch (AbandonedMutexException) { acquired = true; }
                if (!acquired) return 0;
                try
                {
                    var journal = new RecoveryJournal(HostSettings.DirectoryPath);
                    string prior = journal.Read();
                    // If the process crashes after accepting ownership, its marker must be dirty.
                    if (prior == "owned") journal.Write("dirty");
                    using (var adapter = new IsolatedUiAdapter())
                    using (var broker = new Broker(new LeaseController(adapter, settings.AutomationEnabled,
                        prior != "none", prior == "owned"), journal, PipeName(), settings, adapter.EndOperation)) broker.Run();
                    return 0;
                }
                finally { mutex.ReleaseMutex(); }
            }
        }

        private static int RunNative(string origin)
        {
            using (Stream input = Console.OpenStandardInput())
            using (Stream output = Console.OpenStandardOutput())
            {
                byte[] first = NativeProtocol.ReadPayload(input);
                if (first == null) return 0;
                NativeProtocol.ParseRequest(first);
                using (var pipe = new NamedPipeClientStream(".", PipeName(), PipeDirection.InOut, PipeOptions.Asynchronous))
                {
                    try { pipe.Connect(200); }
                    catch (TimeoutException)
                    {
                        Process.Start(new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName, "--broker") {
                            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                            WorkingDirectory = HostSettings.DirectoryPath
                        });
                        pipe.Connect(5000);
                    }
                    if (!LocalPeer.IsOurExecutable(pipe, true)) return 2;
                    NativeProtocol.WritePayload(pipe, Encoding.UTF8.GetBytes(origin));
                    byte[] request = first;
                    while (request != null)
                    {
                        NativeProtocol.ParseRequest(request);
                        NativeProtocol.WritePayload(pipe, request);
                        byte[] response = IsolatedUiAdapter.ReadBounded(pipe, 45000);
                        if (response == null) return 2;
                        NativeProtocol.WritePayload(output, response);
                        request = NativeProtocol.ReadPayload(input);
                    }
                }
            }
            return 0;
        }
    }
}
