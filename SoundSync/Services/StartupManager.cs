using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace SoundSync.Services
{
    /// <summary>
    /// Registers the app to launch when the user signs in, using the per-user Run key.
    /// HKEY_CURRENT_USER needs no administrator rights, and the entry follows the user
    /// rather than the machine.
    /// </summary>
    public static class StartupManager
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "SoundSync";

        /// <summary>Argument that tells a fresh instance to connect by itself.</summary>
        public const string AutoConnectArgument = "--autoconnect";

        /// <summary>Full path of the running executable, quoted for the registry.</summary>
        private static string ExecutablePath
        {
            get
            {
                string path = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
                return path;
            }
        }

        /// <summary>True when this process was started with the auto-connect argument.</summary>
        public static bool LaunchedForAutoConnect()
        {
            foreach (string arg in Environment.GetCommandLineArgs())
                if (string.Equals(arg, AutoConnectArgument, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        /// <summary>Whether Windows is currently set to start this app at sign-in.</summary>
        public static bool IsEnabled()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
                return key?.GetValue(ValueName) is string value && value.Length > 0;
            }
            catch { return false; }
        }

        /// <summary>True when the registered command still points at this executable.</summary>
        public static bool IsCurrent()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
                if (key?.GetValue(ValueName) is not string value) return false;
                return value.IndexOf(ExecutablePath, StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch { return false; }
        }

        /// <summary>
        /// Adds or removes the sign-in entry. Returns an empty string on success, or a
        /// sentence explaining what went wrong.
        /// </summary>
        public static string SetEnabled(bool enabled)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                                ?? Registry.CurrentUser.CreateSubKey(RunKey);
                if (key == null) return "Windows would not open the startup registry key for your account.";

                if (!enabled)
                {
                    if (key.GetValue(ValueName) != null) key.DeleteValue(ValueName, throwOnMissingValue: false);
                    return string.Empty;
                }

                string exe = ExecutablePath;
                if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
                    return "Could not work out where this program is on disk, so it cannot be registered.";

                key.SetValue(ValueName, $"\"{exe}\" {AutoConnectArgument}", RegistryValueKind.String);
                return string.Empty;
            }
            catch (UnauthorizedAccessException)
            {
                return "Windows denied write access to your account's startup list. A policy or security " +
                       "product may be blocking programs from adding themselves to startup.";
            }
            catch (Exception ex)
            {
                return $"Could not update the startup entry: {ex.Message}";
            }
        }
    }
}
