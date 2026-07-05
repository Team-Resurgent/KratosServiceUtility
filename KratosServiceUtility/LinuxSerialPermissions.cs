using System.Diagnostics;
using System.Runtime.InteropServices;

namespace KratosServiceUtility
{
    /// <summary>
    /// Helpers for granting the current Linux user access to a serial port.
    /// Serial device nodes (e.g. /dev/ttyACM0) are root-owned and gated behind a
    /// group (usually 'dialout'/'uucp'), so access requires a one-time privileged
    /// action. This installs a persistent udev rule for Espressif devices and also
    /// fixes up the currently-connected node so the running session works immediately.
    /// </summary>
    internal static class LinuxSerialPermissions
    {
        // Espressif's USB vendor id. Covers ESP32-S3 native USB Serial/JTAG and
        // the common on-board USB-UART bridges shipped on their dev kits.
        private const string EspressifVendorId = "303a";
        private const string UdevRulePath = "/etc/udev/rules.d/99-kratos-esp.rules";

        [DllImport("libc", SetLastError = true)]
        private static extern int access(string pathname, int mode);

        private const int R_OK = 4; // test for read permission
        private const int W_OK = 2; // test for write permission

        /// <summary>
        /// True if the current user can already open <paramref name="portName"/> for read/write.
        /// Uses access(2), so it does NOT open the device (no DTR/RTS toggle, no reset).
        /// Always true on non-Linux, or if the probe cannot be performed (never nag on doubt).
        /// </summary>
        public static bool CanAccess(string? portName)
        {
            if (!OperatingSystem.IsLinux() || string.IsNullOrEmpty(portName))
            {
                return true;
            }
            try
            {
                return access(portName, R_OK | W_OK) == 0;
            }
            catch
            {
                return true;
            }
        }

        /// <summary>
        /// True if <paramref name="ex"/> (or an inner exception) is a serial-port
        /// access-denied error, i.e. the user lacks permission to open the node.
        /// </summary>
        public static bool IsPermissionDenied(Exception? ex)
        {
            for (var e = ex; e != null; e = e.InnerException)
            {
                if (e is UnauthorizedAccessException)
                {
                    return true;
                }
                if (e.Message.Contains("Permission denied", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Attempts to grant the current user access to <paramref name="portName"/> via a
        /// single privileged (pkexec) action. Returns success plus a human-readable detail.
        /// </summary>
        public static async Task<(bool ok, string detail)> FixAsync(string portName)
        {
            if (!OperatingSystem.IsLinux())
            {
                return (false, "Automatic permission fixing is only supported on Linux.");
            }

            string user = Environment.UserName;

            // Runs as root via pkexec:
            //  1. install a persistent udev rule granting the local desktop user access
            //     to any Espressif device (uaccess), plus a permissive mode as a fallback,
            //  2. reload/trigger udev so the rule applies,
            //  3. grant access to the *current* node immediately (no replug needed),
            //     preferring an ACL for just this user, falling back to chmod.
            string script =
                $"set -e; " +
                $"printf '%s\\n' 'SUBSYSTEM==\"tty\", SUBSYSTEMS==\"usb\", ATTRS{{idVendor}}==\"{EspressifVendorId}\", TAG+=\"uaccess\", MODE=\"0666\"' > '{UdevRulePath}'; " +
                $"udevadm control --reload-rules; " +
                $"udevadm trigger; " +
                $"if command -v setfacl >/dev/null 2>&1; then setfacl -m u:{user}:rw '{portName}'; else chmod a+rw '{portName}'; fi";

            var psi = new ProcessStartInfo
            {
                FileName = "pkexec",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            psi.ArgumentList.Add("sh");
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(script);

            try
            {
                using var proc = Process.Start(psi);
                if (proc == null)
                {
                    return (false, "Could not start pkexec.");
                }

                string stderr = await proc.StandardError.ReadToEndAsync();
                await proc.WaitForExitAsync();

                if (proc.ExitCode == 0)
                {
                    return (true, "Permissions updated.");
                }

                // pkexec: 126 = user dismissed/not authorized, 127 = auth failed or pkexec error.
                string reason = proc.ExitCode switch
                {
                    126 => "The request was dismissed or not authorized.",
                    127 => "Authentication failed, or pkexec is not installed.",
                    _ => string.IsNullOrWhiteSpace(stderr) ? $"pkexec exited with code {proc.ExitCode}." : stderr.Trim(),
                };
                return (false, reason);
            }
            catch (Exception ex)
            {
                // pkexec missing entirely lands here.
                return (false,
                    $"{ex.Message}\n\nManual fix:\n  sudo usermod -aG dialout $USER\nthen log out and back in.");
            }
        }
    }
}
