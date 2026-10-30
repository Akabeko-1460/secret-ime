using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace SecretIme
{
    public interface IPrivacyAdapter
    {
        bool? Read();
        bool Enable();
        bool Restore();
    }

    public sealed class CoreStatus
    {
        public string State { get; internal set; }
        public string Detail { get; internal set; }
        public int ActiveClients { get; internal set; }
        public bool? ModeEnabled { get; internal set; }
        public bool RestoreRequired { get; internal set; }
        public string Effectiveness { get { return "unverified"; } }

        public Dictionary<string, object> ToDictionary()
        {
            return new Dictionary<string, object> {
                { "v", 1 }, { "state", State }, { "detail", Detail },
                { "effectiveness", Effectiveness }, { "restoreRequired", RestoreRequired },
                { "activeClients", ActiveClients }, { "modeEnabled", ModeEnabled }
            };
        }
    }

    // Connection identities belong to the broker, never the browser request.
    public sealed class LeaseController
    {
        private readonly object gate = new object();
        private readonly HashSet<string> clients = new HashSet<string>(StringComparer.Ordinal);
        private readonly IPrivacyAdapter adapter;
        private readonly bool configured;
        private bool ownsChange, recoveryRequired, observedOnDuringLease;
        private bool? currentMode;
        private string recoveryDetail;

        public LeaseController(IPrivacyAdapter adapter, bool configured,
            bool recoveryRequired = false, bool restoreOwned = false)
        {
            if (adapter == null) throw new ArgumentNullException("adapter");
            this.adapter = adapter;
            this.configured = configured;
            this.recoveryRequired = recoveryRequired;
            // Only a clean broker handoff may preserve explicit-restore ownership.
            // Dirty/crashed sessions must pass restoreOwned=false.
            ownsChange = configured && recoveryRequired && restoreOwned;
            recoveryDetail = ownsChange ? "previous-session-explicit-restore-needed" :
                (recoveryRequired ? "previous-session-needs-manual-review" : "");
        }

        public bool HasOwnedChange { get { lock (gate) return ownsChange; } }

        public CoreStatus Sync(string clientId, bool active)
        {
            if (String.IsNullOrEmpty(clientId) || clientId.Length > 128)
                throw new ArgumentException("Invalid broker client identity");
            lock (gate)
            {
                if (active)
                {
                    if (!clients.Contains(clientId) && clients.Count >= 64)
                        throw new InvalidOperationException("Client limit reached");
                    clients.Add(clientId);
                }
                else clients.Remove(clientId);
                return Evaluate(active);
            }
        }

        public CoreStatus Disconnect(string clientId) { return Sync(clientId, false); }
        public CoreStatus Status() { lock (gate) return Evaluate(false); }

        // Explicit user intent is required; this is never called by disconnect cleanup.
        public CoreStatus Restore()
        {
            lock (gate)
            {
                Observe();
                if (!configured) return BuildStatus();
                if (clients.Count != 0) return BuildStatus("active-requests-prevent-restore");
                if (currentMode == false)
                {
                    ownsChange = false;
                    recoveryRequired = false;
                    recoveryDetail = "";
                    return BuildStatus("already-disabled");
                }
                if (currentMode == null || !ownsChange)
                {
                    if (recoveryRequired || currentMode == null)
                        NeedRecovery("ownership-unknown-use-ime-settings");
                    return BuildStatus("pre-existing-or-unknown-mode-preserved");
                }
                bool restored = TryWrite(false);
                currentMode = TryRead();
                if (restored && currentMode == false)
                {
                    ownsChange = false;
                    recoveryRequired = false;
                    recoveryDetail = "";
                    return BuildStatus("explicit-restore-confirmed");
                }
                // Interrupted writes lose ownership; never retry OFF automatically.
                ownsChange = false;
                NeedRecovery("restore-unconfirmed-use-ime-settings");
                return BuildStatus();
            }
        }

        private CoreStatus Evaluate(bool mayEnable)
        {
            Observe();
            if (!configured) return BuildStatus();
            if (clients.Count > 0 && mayEnable && !recoveryRequired && currentMode == false)
            {
                bool enabled = TryWrite(true);
                currentMode = TryRead();
                if (enabled && currentMode == true)
                {
                    ownsChange = true;
                    observedOnDuringLease = true;
                }
                else NeedRecovery("enable-unconfirmed-use-ime-settings");
            }
            if (clients.Count == 0 && ownsChange)
                NeedRecovery("last-request-ended-explicit-restore-needed");
            return BuildStatus();
        }

        private void Observe()
        {
            if (!configured) { currentMode = null; return; }
            currentMode = TryRead();
            if ((ownsChange || observedOnDuringLease) && currentMode != true)
            {
                ownsChange = false;
                NeedRecovery(currentMode == false ? "manual-change-preserved" : "state-unknown");
            }
            if (clients.Count > 0 && currentMode == true) observedOnDuringLease = true;
            if (clients.Count == 0) observedOnDuringLease = false;
        }

        private bool? TryRead()
        {
            try { return adapter.Read(); }
            catch (Exception) { return null; }
        }

        private bool TryWrite(bool enable)
        {
            try { return enable ? adapter.Enable() : adapter.Restore(); }
            catch (Exception) { return false; }
        }

        private void NeedRecovery(string detail) { recoveryRequired = true; recoveryDetail = detail; }

        private CoreStatus BuildStatus(string detail = null)
        {
            string state;
            if (!configured) state = "not-configured";
            else if (recoveryRequired) state = "recovery-needed";
            else if (currentMode == null) state = "unavailable";
            else if (clients.Count > 0 && currentMode == true) state = "mode-enabled";
            else if (clients.Count > 0) state = "unavailable";
            else state = "idle";
            return new CoreStatus {
                State = state,
                Detail = detail ?? (recoveryRequired ? recoveryDetail :
                    (state == "unavailable" ? "state-unconfirmed" : "mode-effectiveness-unverified")),
                ActiveClients = clients.Count,
                ModeEnabled = currentMode,
                RestoreRequired = recoveryRequired && currentMode != false
            };
        }
    }

    public sealed class NativeRequest
    {
        public string Operation { get; internal set; }
        public bool Active { get; internal set; }
    }

    public static class NativeProtocol
    {
        public const int MaxMessageBytes = 2048;
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        public static NativeRequest ReadRequest(Stream input)
        {
            byte[] payload = ReadPayload(input);
            return payload == null ? null : ParseRequest(payload);
        }

        public static byte[] ReadPayload(Stream input)
        {
            int first = input.ReadByte();
            if (first == -1) return null;
            byte[] header = new byte[4];
            header[0] = (byte)first;
            ReadFully(input, header, 1, 3);
            uint length = (uint)header[0] | ((uint)header[1] << 8) |
                ((uint)header[2] << 16) | ((uint)header[3] << 24);
            if (length == 0 || length > MaxMessageBytes)
                throw new InvalidDataException("Invalid native message length");
            byte[] payload = new byte[(int)length];
            ReadFully(input, payload, 0, payload.Length);
            return payload;
        }

        public static NativeRequest ParseRequest(byte[] payload)
        {
            if (payload == null || payload.Length == 0 || payload.Length > MaxMessageBytes)
                throw new InvalidDataException("Invalid native message size");
            try
            {
                string json = StrictUtf8.GetString(payload);
                // JavaScriptSerializer alone accepts duplicate keys and JavaScript syntax.
                Dictionary<string, object> values = new FlatJsonParser(json).Parse();
                object version, operation;
                if (!values.TryGetValue("v", out version) || !(version is int) || (int)version != 1 ||
                    !values.TryGetValue("op", out operation) || !(operation is string))
                    throw new InvalidDataException("Invalid protocol version or operation");
                string op = (string)operation;
                if (op != "sync" && op != "status" && op != "restore")
                    throw new InvalidDataException("Unknown operation");
                object active;
                if (op == "sync")
                {
                    if (values.Count != 3 || !values.TryGetValue("active", out active) || !(active is bool))
                        throw new InvalidDataException("sync requires a boolean active value");
                    return new NativeRequest { Operation = op, Active = (bool)active };
                }
                if (values.Count != 2) throw new InvalidDataException("Unexpected request member");
                return new NativeRequest { Operation = op, Active = false };
            }
            catch (InvalidDataException) { throw; }
            catch (Exception error)
            {
                if (error is DecoderFallbackException || error is ArgumentException ||
                    error is InvalidOperationException || error is FormatException || error is OverflowException)
                    throw new InvalidDataException("Invalid native request JSON");
                throw;
            }
        }

        public static void WriteStatus(Stream output, CoreStatus status)
        {
            if (status == null) throw new ArgumentNullException("status");
            WritePayload(output, StrictUtf8.GetBytes(new JavaScriptSerializer().Serialize(status.ToDictionary())));
        }

        public static void WritePayload(Stream output, byte[] payload)
        {
            if (payload == null || payload.Length == 0 || payload.Length > MaxMessageBytes)
                throw new InvalidDataException("Invalid native response size");
            int length = payload.Length;
            byte[] header = { (byte)length, (byte)(length >> 8), (byte)(length >> 16), (byte)(length >> 24) };
            output.Write(header, 0, header.Length);
            output.Write(payload, 0, payload.Length);
            output.Flush();
        }

        private static void ReadFully(Stream input, byte[] buffer, int offset, int count)
        {
            while (count > 0)
            {
                int received = input.Read(buffer, offset, count);
                if (received == 0) throw new InvalidDataException("Truncated native message");
                offset += received;
                count -= received;
            }
        }

        // This schema deliberately permits only three keys and string/true/false/1 values.
        private sealed class FlatJsonParser
        {
            private readonly string text;
            private int position;
            public FlatJsonParser(string text) { this.text = text; }

            public Dictionary<string, object> Parse()
            {
                var result = new Dictionary<string, object>(StringComparer.Ordinal);
                Expect('{'); SkipSpace();
                if (Take('}')) { End(); return result; }
                while (true)
                {
                    string key = ReadString();
                    if (key != "v" && key != "op" && key != "active") Fail();
                    if (result.ContainsKey(key)) Fail();
                    Expect(':'); result.Add(key, ReadValue()); SkipSpace();
                    if (Take('}')) break;
                    Expect(',');
                }
                End(); return result;
            }

            private object ReadValue()
            {
                SkipSpace();
                if (position >= text.Length) { Fail(); return null; }
                if (text[position] == '"') return ReadString();
                int start = position;
                while (position < text.Length && text[position] != ',' && text[position] != '}' &&
                    !IsSpace(text[position])) position++;
                string token = text.Substring(start, position - start);
                if (token == "true") return true;
                if (token == "false") return false;
                if (token == "1") return 1;
                Fail(); return null;
            }

            private string ReadString()
            {
                SkipSpace(); int start = position;
                if (!Take('"')) Fail();
                while (position < text.Length)
                {
                    char current = text[position++];
                    if (current == '"')
                        return new JavaScriptSerializer().Deserialize<string>(text.Substring(start, position - start));
                    if (current < 0x20) Fail();
                    if (current != '\\') continue;
                    if (position >= text.Length) Fail();
                    char escaped = text[position++];
                    if (escaped == 'u')
                    {
                        for (int i = 0; i < 4; i++)
                            if (position >= text.Length || !IsHex(text[position++])) Fail();
                    }
                    else if (escaped != '"' && escaped != '\\' && escaped != '/' && escaped != 'b' &&
                        escaped != 'f' && escaped != 'n' && escaped != 'r' && escaped != 't') Fail();
                }
                Fail(); return null;
            }

            private void End() { SkipSpace(); if (position != text.Length) Fail(); }
            private void Expect(char expected) { SkipSpace(); if (!Take(expected)) Fail(); }
            private bool Take(char expected)
            {
                if (position >= text.Length || text[position] != expected) return false;
                position++; return true;
            }
            private void SkipSpace() { while (position < text.Length && IsSpace(text[position])) position++; }
            private static bool IsSpace(char value) { return value == ' ' || value == '\t' || value == '\r' || value == '\n'; }
            private static bool IsHex(char value)
            {
                return (value >= '0' && value <= '9') || (value >= 'a' && value <= 'f') ||
                    (value >= 'A' && value <= 'F');
            }
            private static void Fail() { throw new InvalidDataException("Invalid request JSON schema"); }
        }
    }
}
