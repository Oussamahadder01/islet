using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security;
using Microsoft.Win32;

namespace Islet
{
    // Per-user installation that keeps Islet running like a system component:
    // the exe lives in %LOCALAPPDATA%\Programs\Islet, a Task Scheduler task
    // starts it at sign-in and re-checks every minute (the in-process hot
    // standby covers crashes instantly), and Windows lists it under
    // Settings > Apps with a working uninstaller. Nothing needs administrator rights.
    public static class Installer
    {
        public const string TaskName = "Islet";
        private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Islet";
        private const string LegacyRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        public static readonly string InstallDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Islet");
        public static readonly string InstalledExe = Path.Combine(InstallDir, "Islet.exe");
        private static readonly string Shortcut = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Islet.lnk");

        public static string CurrentExe { get { return Assembly.GetEntryAssembly().Location; } }

        public static bool IsInstalled { get { return File.Exists(InstalledExe) && TaskExists(); } }

        public static void Install()
        {
            StopEverything();
            Directory.CreateDirectory(InstallDir);
            if (!string.Equals(Path.GetFullPath(CurrentExe), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase))
                File.Copy(CurrentExe, InstalledExe, true);

            RegisterTask();
            CreateShortcut();
            WriteUninstallEntry();
            RemoveLegacyRunValue();
        }

        public static void Uninstall()
        {
            StopEverything();
            try { WithTaskFolder(folder => Invoke(folder, "DeleteTask", TaskName, 0)); }
            catch (Exception ex) { Log.Error("delete task", ex); }
            TryDelete(Shortcut);
            Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);
            RemoveLegacyRunValue();

            // A running exe cannot delete itself; a hidden shell removes the
            // folder a moment after this process exits.
            if (Directory.Exists(InstallDir))
            {
                Process.Start(new ProcessStartInfo("cmd.exe",
                    "/c ping 127.0.0.1 -n 3 >nul & rmdir /s /q \"" + InstallDir + "\"")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WindowStyle = ProcessWindowStyle.Hidden,
                });
            }
        }

        // Starts the installed copy under the scheduler, so restarts apply to it.
        public static void RunTask()
        {
            WithTaskFolder(folder =>
            {
                object task = Invoke(folder, "GetTask", TaskName);
                Invoke(task, "Run", (object)null);
                return null;
            });
        }

        // "Start with Windows": enables or disables the task, keeping it registered.
        public static bool StartsWithWindows
        {
            get
            {
                try
                {
                    return (bool)WithTaskFolder(folder => Get(Invoke(folder, "GetTask", TaskName), "Enabled"));
                }
                catch (Exception)
                {
                    return false;
                }
            }
            set
            {
                WithTaskFolder(folder =>
                {
                    object task = Invoke(folder, "GetTask", TaskName);
                    task.GetType().InvokeMember("Enabled", BindingFlags.SetProperty, null, task, new object[] { value });
                    return null;
                });
            }
        }

        private static bool TaskExists()
        {
            try
            {
                WithTaskFolder(folder => Invoke(folder, "GetTask", TaskName));
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static void RegisterTask()
        {
            string user = SecurityElement.Escape(Environment.UserDomainName + "\\" + Environment.UserName);
            string exe = SecurityElement.Escape(InstalledExe);
            // A logon trigger's repetition only arms after that logon happens, so the
            // every-minute safety net hangs off a time trigger that starts now.
            string now = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");
            string xml =
@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.4"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <RegistrationInfo><Description>Keeps the Islet dynamic island running.</Description></RegistrationInfo>
  <Triggers>
    <LogonTrigger>
      <Enabled>true</Enabled>
      <UserId>" + user + @"</UserId>
    </LogonTrigger>
    <TimeTrigger>
      <Enabled>true</Enabled>
      <StartBoundary>" + now + @"</StartBoundary>
      <Repetition><Interval>PT1M</Interval><StopAtDurationEnd>false</StopAtDurationEnd></Repetition>
    </TimeTrigger>
  </Triggers>
  <Principals>
    <Principal id=""Author""><UserId>" + user + @"</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>true</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>4</Priority>
    <RestartOnFailure><Interval>PT1M</Interval><Count>999</Count></RestartOnFailure>
  </Settings>
  <Actions Context=""Author""><Exec><Command>""" + exe + @"""</Command></Exec></Actions>
</Task>";
            // 6 = create or update, 3 = interactive token (runs as the signed-in user).
            WithTaskFolder(folder => Invoke(folder, "RegisterTask", TaskName, xml, 6, null, null, 3, null));
        }

        private static void CreateShortcut()
        {
            object shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
            object link = Invoke(shell, "CreateShortcut", Shortcut);
            Set(link, "TargetPath", InstalledExe);
            Set(link, "WorkingDirectory", InstallDir);
            Set(link, "Description", "Dynamic island for Windows");
            Invoke(link, "Save");
        }

        private static void WriteUninstallEntry()
        {
            using (var key = Registry.CurrentUser.CreateSubKey(UninstallKey))
            {
                key.SetValue("DisplayName", "Islet");
                key.SetValue("DisplayVersion", "3.0");
                key.SetValue("DisplayIcon", InstalledExe);
                key.SetValue("InstallLocation", InstallDir);
                key.SetValue("UninstallString", "\"" + InstalledExe + "\" --uninstall");
                key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                key.SetValue("EstimatedSize", (int)(new FileInfo(InstalledExe).Length / 1024), RegistryValueKind.DWord);
            }
        }

        // Earlier versions offered a Run-key "launch at login"; the task replaces it.
        private static void RemoveLegacyRunValue()
        {
            using (var key = Registry.CurrentUser.OpenSubKey(LegacyRunKey, true))
                if (key != null) key.DeleteValue("Islet", false);
        }

        // Pause the task and dismiss the standby first, or the standby would
        // take over the moment the owner is stopped (which is its whole job).
        private static void StopEverything()
        {
            try { if (TaskExists()) StartsWithWindows = false; }
            catch (Exception ex) { Log.Error("pause task", ex); }
            using (var quit = new System.Threading.EventWaitHandle(false, System.Threading.EventResetMode.ManualReset, Program.QuitEventName))
            {
                quit.Set();
                System.Threading.Thread.Sleep(300);
                StopOtherInstances();
            }
        }

        private static void StopOtherInstances()
        {
            int self = Process.GetCurrentProcess().Id;
            foreach (var process in Process.GetProcessesByName("Islet"))
            {
                if (process.Id == self) continue;
                try
                {
                    process.Kill();
                    process.WaitForExit(5000);
                }
                catch (Exception ex)
                {
                    Log.Error("stop instance " + process.Id, ex);
                }
            }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception ex) { Log.Error("delete " + path, ex); }
        }

        private static object WithTaskFolder(Func<object, object> action)
        {
            object service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service"));
            Invoke(service, "Connect");
            object folder = Invoke(service, "GetFolder", "\\");
            return action(folder);
        }

        private static object Invoke(object target, string name, params object[] args)
        {
            return target.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, target, args);
        }

        private static object Get(object target, string name)
        {
            return target.GetType().InvokeMember(name, BindingFlags.GetProperty, null, target, null);
        }

        private static void Set(object target, string name, object value)
        {
            target.GetType().InvokeMember(name, BindingFlags.SetProperty, null, target, new[] { value });
        }
    }
}
