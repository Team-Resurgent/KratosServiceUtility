using Avalonia;
using System;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace KratosServiceUtility
{
    internal class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            TrySetLinuxScaleFromGnome();
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }

        public static AppBuilder BuildAvaloniaApp()
            => AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .WithInterFont()
                .LogToTrace();

        // On a Wayland session Avalonia runs under XWayland, whose X11 DPI probe reports Xft.dpi=96, so on
        // a scaled GNOME desktop every window renders at 1x (~half size on a 200% display). GNOME doesn't
        // expose that scale to XWayland clients, but Mutter knows it -- read the real per-monitor scale and
        // hand it to Avalonia via AVALONIA_GLOBAL_SCALE_FACTOR. No-op on Windows/macOS, if the user already
        // set the variable, or if anything goes wrong (then Avalonia's own detection stands).
        private static void TrySetLinuxScaleFromGnome()
        {
            if (!OperatingSystem.IsLinux()) return;
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AVALONIA_GLOBAL_SCALE_FACTOR"))) return;

            double scale = QueryMutterScale();
            if (scale <= 1.0) return; // 1x or unknown -- leave Avalonia to its own devices
            Environment.SetEnvironmentVariable(
                "AVALONIA_GLOBAL_SCALE_FACTOR", scale.ToString("0.####", CultureInfo.InvariantCulture));
        }

        private static double QueryMutterScale()
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "gdbus",
                    Arguments = "call --session --dest org.gnome.Mutter.DisplayConfig "
                              + "--object-path /org/gnome/Mutter/DisplayConfig "
                              + "--method org.gnome.Mutter.DisplayConfig.GetCurrentState",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                if (proc == null) return 0;
                string outp = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(2000);

                // Logical monitors print as: (x, y, scale, uint32 transform, primary, [connectors], {props}).
                // The leading two ints + double + "uint32" pins this to logical monitors (the monitors and
                // modes arrays start with a nested tuple / a string, so they can't match). Prefer the
                // primary monitor's scale; otherwise take the first logical monitor found.
                double first = 0;
                foreach (Match m in Regex.Matches(outp,
                    @"\(\s*-?\d+,\s*-?\d+,\s*([0-9]+(?:\.[0-9]+)?),\s*uint32\s+\d+,\s*(true|false)"))
                {
                    double s = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                    if (first == 0) first = s;
                    if (m.Groups[2].Value == "true") return s; // primary monitor wins
                }
                return first;
            }
            catch
            {
                return 0;
            }
        }
    }
}
