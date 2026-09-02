using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace EventLogViewer.Core.Hosting
{
    /// <summary>
    /// What kind of session the app is running in, resolved once at startup.
    /// </summary>
    /// <remarks>
    /// ScreenConnect Backstage runs a process as NT AUTHORITY\SYSTEM on a separate desktop
    /// object, which breaks a handful of assumptions that hold on a normal interactive desktop:
    /// shell common dialogs are unreliable there, launching a browser is either a no-op or spawns
    /// one as SYSTEM, and there is no user profile to write to. Detecting the situation lets the
    /// UI pick safe alternatives instead of failing in ways that are hard to diagnose over a
    /// remote session.
    /// </remarks>
    public sealed class HostEnvironment
    {
        private HostEnvironment() { }

        /// <summary>Running as the machine account (SYSTEM).</summary>
        public bool IsSystemAccount { get; private set; }

        /// <summary>Member of the local Administrators group, or SYSTEM.</summary>
        public bool IsElevated { get; private set; }

        /// <summary>The thread's desktop is not the standard interactive "Default" desktop.</summary>
        public bool IsAlternateDesktop { get; private set; }

        /// <summary>Name of the current desktop object, for the status bar. May be empty.</summary>
        public string DesktopName { get; private set; }

        public string UserName { get; private set; }

        /// <summary>
        /// Best guess that we are in Backstage or something equivalent. Either signal alone is
        /// enough: a SYSTEM process has the profile and browser problems regardless of desktop,
        /// and an alternate desktop has the dialog problems regardless of account.
        /// </summary>
        public bool IsBackstageLikely => IsSystemAccount || IsAlternateDesktop;

        /// <summary>
        /// Where to write exports when a file dialog cannot be trusted. Deliberately a fixed,
        /// predictable path under the Windows temp directory so a technician can retrieve it with
        /// ScreenConnect file transfer without having to be told where to look.
        /// </summary>
        public string FallbackExportDirectory =>
            Path.Combine(Path.GetTempPath(), "EventLogViewer");

        /// <summary>One-line summary for the status bar, so the chosen mode is visible in the field
        /// rather than being an invisible decision that only shows up as odd behaviour.</summary>
        public string Describe()
        {
            var who = IsSystemAccount ? "SYSTEM" : (UserName ?? "user");
            if (!IsSystemAccount && IsElevated) who += " (elevated)";

            return IsAlternateDesktop
                ? who + " · " + (string.IsNullOrEmpty(DesktopName) ? "alternate desktop" : DesktopName + " desktop")
                : who;
        }

        /// <summary>
        /// Builds a specific environment, so the Backstage fallbacks can be tested without a
        /// Backstage session to run in. Those fallbacks are the whole point of the class and
        /// otherwise could not be verified until someone tried it on a customer's machine.
        /// </summary>
        internal static HostEnvironment CreateForTesting(bool isSystemAccount, bool isElevated,
                                                         bool isAlternateDesktop,
                                                         string desktopName = null,
                                                         string userName = null)
        {
            return new HostEnvironment
            {
                IsSystemAccount = isSystemAccount,
                IsElevated = isElevated,
                IsAlternateDesktop = isAlternateDesktop,
                DesktopName = desktopName,
                UserName = userName
            };
        }

        public static HostEnvironment Detect()
        {
            var env = new HostEnvironment();

            try
            {
                var identity = WindowsIdentity.GetCurrent();
                env.UserName = identity.Name;
                env.IsSystemAccount = identity.IsSystem;
                env.IsElevated = identity.IsSystem ||
                                 new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                // Identity is unavailable in some restricted contexts; the defaults (not system,
                // not elevated) are the safe assumption because they keep the elevation warnings on.
            }

            try
            {
                env.DesktopName = GetCurrentDesktopName();
                env.IsAlternateDesktop = !string.IsNullOrEmpty(env.DesktopName) &&
                                         !string.Equals(env.DesktopName, "Default", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                env.IsAlternateDesktop = false;
            }

            return env;
        }

        private const int UOI_NAME = 2;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr GetThreadDesktop(uint dwThreadId);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetUserObjectInformation(IntPtr hObj, int nIndex, StringBuilder pvInfo,
                                                            uint nLength, out uint lpnLengthNeeded);

        private static string GetCurrentDesktopName()
        {
            var desktop = GetThreadDesktop(GetCurrentThreadId());
            if (desktop == IntPtr.Zero) return null;

            var buffer = new StringBuilder(256);
            return GetUserObjectInformation(desktop, UOI_NAME, buffer, (uint)(buffer.Capacity * sizeof(char)), out _)
                ? buffer.ToString()
                : null;
        }
    }
}
