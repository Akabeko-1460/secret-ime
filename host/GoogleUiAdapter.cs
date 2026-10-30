using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Windows.Automation;

namespace SecretIme
{
    // This adapter reads only the named setting in a newly opened, trusted dialog.
    // No keystrokes, candidate text, dictionary files, or private IPC are used.
    public sealed class GoogleUiAdapter : IPrivacyAdapter
    {
        private readonly Action beforeWrite;
        private bool? cachedMode;
        public string LastDetail { get; private set; }

        public GoogleUiAdapter(Action beforeWrite)
        {
            this.beforeWrite = beforeWrite;
            LastDetail = "not-observed";
        }

        public bool? Read()
        {
            try
            {
                using (DialogSession dialog = DialogSession.Open())
                {
                    if (dialog == null) return Unknown("official-settings-unavailable");
                    cachedMode = dialog.ReadMode();
                    LastDetail = cachedMode.HasValue ? "official-ui-read" : "checkbox-not-readable";
                    return cachedMode;
                }
            }
            catch (Exception) { return Unknown("official-ui-read-failed"); }
        }

        public bool Enable() { return SetMode(true); }
        public bool Restore() { return SetMode(false); }

        private bool? Unknown(string detail)
        {
            cachedMode = null;
            LastDetail = detail;
            return null;
        }

        private bool SetMode(bool desired)
        {
            try
            {
                using (DialogSession dialog = DialogSession.Open())
                {
                    if (dialog == null) { Unknown("official-settings-unavailable"); return false; }
                    bool? before = dialog.ReadMode();
                    if (!before.HasValue) { Unknown("checkbox-not-readable"); return false; }
                    // A concurrent manual change invalidates the observation used by the core.
                    if (!cachedMode.HasValue || before != cachedMode)
                    {
                        Unknown("concurrent-settings-change");
                        return false;
                    }
                    if (before == desired) return true;
                    // Persist recovery intent before touching the checkbox or applying it.
                    beforeWrite();
                    if (!dialog.ChangeMode(desired)) { Unknown("official-ui-write-unconfirmed"); return false; }
                }
                // A fresh dialog must reload the applied configuration, not just show our edit.
                bool confirmed = Read() == desired;
                if (!confirmed) LastDetail = "official-ui-reload-unconfirmed";
                return confirmed;
            }
            catch (Exception) { Unknown("official-ui-write-failed"); return false; }
        }

        private sealed class DialogSession : IDisposable
        {
            private readonly Process process;
            private readonly AutomationElement window;
            private AutomationElement checkbox;
            private bool closed;

            private DialogSession(Process process, AutomationElement window)
            {
                this.process = process;
                this.window = window;
            }

            public static DialogSession Open()
            {
                string executable = TrustedToolPath();
                if (executable == null) return null;
                // Never reuse or close a dialog that the user might be editing.
                foreach (Process existing in Process.GetProcessesByName("GoogleIMEJaTool"))
                {
                    using (existing)
                        if (existing.SessionId == Process.GetCurrentProcess().SessionId) return null;
                }
                Process process = Process.Start(new ProcessStartInfo(executable, "--mode=config_dialog") {
                    UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(executable),
                    WindowStyle = ProcessWindowStyle.Normal
                });
                if (process == null) return null;
                Stopwatch deadline = Stopwatch.StartNew();
                while (deadline.ElapsedMilliseconds < 4000 && !process.HasExited)
                {
                    foreach (IntPtr handle in NativeWindows.ForProcess(process.Id))
                    {
                        if (!NativeWindows.IsWindowVisible(handle)) continue;
                        AutomationElement window = AutomationElement.FromHandle(handle);
                        if (window == null || window.Current.ProcessId != process.Id) continue;
                        var session = new DialogSession(process, window);
                        if (session.FindCheckbox() != null) return session;
                    }
                    // Bounded startup wait, never an idle monitoring loop.
                    Thread.Sleep(100);
                }
                // Only our newly started, unedited dialog may receive a close request.
                foreach (IntPtr handle in NativeWindows.ForProcess(process.Id)) NativeWindows.RequestClose(handle);
                process.Dispose();
                return null;
            }

            private AutomationElement FindCheckbox()
            {
                if (checkbox != null) return checkbox;
                const string fullId = "QApplication.ConfigDialog.configDialogTabWidget.qt_tabwidget_stackedwidget.privacyTab.incognitoModeGroup.incognitoModeCheckBox";
                var condition = new AndCondition(new OrCondition(
                    new PropertyCondition(AutomationElement.AutomationIdProperty, "incognitoModeCheckBox"),
                    new PropertyCondition(AutomationElement.AutomationIdProperty, fullId),
                    new PropertyCondition(AutomationElement.NameProperty, fullId)),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.CheckBox));
                // Qt exposes non-selected pages inconsistently. Select only the privacy tab.
                foreach (string name in new[] { "プライバシー", "Privacy" })
                {
                    AutomationElement tab = window.FindFirst(TreeScope.Descendants, new AndCondition(
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem),
                        new PropertyCondition(AutomationElement.NameProperty, name)));
                    object selection;
                    if (tab != null && tab.TryGetCurrentPattern(SelectionItemPattern.Pattern, out selection))
                    {
                        ((SelectionItemPattern)selection).Select();
                        break;
                    }
                }
                AutomationElementCollection matches = window.FindAll(TreeScope.Descendants, condition);
                // Never guess using coordinates or a similarly worded unrelated checkbox.
                if (matches.Count == 1 && matches[0].Current.ProcessId == process.Id)
                    checkbox = matches[0];
                return checkbox;
            }

            public bool? ReadMode()
            {
                AutomationElement control = FindCheckbox();
                object pattern;
                if (control == null || !control.Current.IsEnabled) return null;
                if (!control.TryGetCurrentPattern(TogglePattern.Pattern, out pattern)) return null;
                ToggleState state = ((TogglePattern)pattern).Current.ToggleState;
                if (state == ToggleState.On) return true;
                if (state == ToggleState.Off) return false;
                return null;
            }

            public bool ChangeMode(bool desired)
            {
                bool? current = ReadMode();
                object pattern;
                if (!current.HasValue || !checkbox.TryGetCurrentPattern(TogglePattern.Pattern, out pattern)) return false;
                if (current != desired) ((TogglePattern)pattern).Toggle();
                if (ReadMode() != desired) return false;
                AutomationElementCollection buttons = window.FindAll(TreeScope.Descendants, new AndCondition(
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                    new PropertyCondition(AutomationElement.NameProperty, "OK")));
                if (buttons.Count != 1 || !buttons[0].Current.IsEnabled ||
                    !buttons[0].TryGetCurrentPattern(InvokePattern.Pattern, out pattern)) return false;
                ((InvokePattern)pattern).Invoke();
                process.WaitForExit(2000);
                closed = process.HasExited;
                return closed;
            }

            public void Dispose()
            {
                if (!closed && !process.HasExited)
                {
                    // Closing without OK discards any unconfirmed local checkbox edit.
                    foreach (IntPtr handle in NativeWindows.ForProcess(process.Id)) NativeWindows.RequestClose(handle);
                    process.WaitForExit(1000);
                }
                process.Dispose();
            }
        }

        public static string TrustedToolPath()
        {
            foreach (Environment.SpecialFolder folder in new[] {
                Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.ProgramFiles })
            {
                string path = Path.Combine(Environment.GetFolderPath(folder), "Google", "Google Japanese Input", "GoogleIMEJaTool.exe");
                if (File.Exists(path) && Authenticode.IsGoogleSigned(path)) return path;
            }
            return null;
        }
    }

    internal static class NativeWindows
    {
        private delegate bool EnumWindowCallback(IntPtr window, IntPtr parameter);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowCallback callback, IntPtr parameter);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
        [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PostMessage(IntPtr window, uint message, IntPtr wparam, IntPtr lparam);

        internal static List<IntPtr> ForProcess(int processId)
        {
            var handles = new List<IntPtr>();
            EnumWindows(delegate(IntPtr window, IntPtr unused) {
                uint owner;
                GetWindowThreadProcessId(window, out owner);
                if (owner == (uint)processId) handles.Add(window);
                return true;
            }, IntPtr.Zero);
            return handles;
        }

        internal static void RequestClose(IntPtr window) { PostMessage(window, 0x0010, IntPtr.Zero, IntPtr.Zero); }
    }

    internal static class Authenticode
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct FileInfo
        {
            internal uint Size;
            [MarshalAs(UnmanagedType.LPWStr)] internal string Path;
            internal IntPtr File, Subject;
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct TrustData
        {
            internal uint Size;
            internal IntPtr Policy, Sip;
            internal uint Ui, Revocation, Choice;
            internal IntPtr File;
            internal uint StateAction;
            internal IntPtr State, Url;
            internal uint Flags, Context;
        }
        [DllImport("wintrust.dll", ExactSpelling = true)]
        private static extern int WinVerifyTrust(IntPtr window, [MarshalAs(UnmanagedType.LPStruct)] Guid action, ref TrustData data);

        internal static bool IsGoogleSigned(string path)
        {
            IntPtr filePointer = IntPtr.Zero;
            try
            {
                var file = new FileInfo { Size = (uint)Marshal.SizeOf(typeof(FileInfo)), Path = path };
                filePointer = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(FileInfo)));
                Marshal.StructureToPtr(file, filePointer, false);
                var data = new TrustData {
                    Size = (uint)Marshal.SizeOf(typeof(TrustData)), Ui = 2, Choice = 1,
                    File = filePointer, Flags = 0x1000 // cache-only URL retrieval; no network verification
                };
                if (WinVerifyTrust(new IntPtr(-1), new Guid("00AAC56B-CD44-11D0-8CC2-00C04FC295EE"), ref data) != 0) return false;
                using (var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(path)))
                    return certificate.GetNameInfo(X509NameType.SimpleName, false) == "Google LLC";
            }
            catch (Exception) { return false; }
            finally
            {
                if (filePointer != IntPtr.Zero)
                {
                    Marshal.DestroyStructure(filePointer, typeof(FileInfo));
                    Marshal.FreeHGlobal(filePointer);
                }
            }
        }
    }
}
