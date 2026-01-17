using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ManagedBass;
using System.Diagnostics;
using System.IO.Ports;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace Kratos_Service_utility
{
    public partial class MainWindow : Window
    {
        private readonly int fxW = 96;
        private readonly int fxH = 64;
        private WriteableBitmap? _plasmaBitmap;
        private DispatcherTimer? _plasmaTimer;
        private DateTime _plasmaStart;

        private readonly Color _bgColor = Color.Parse("#0a0a1a");        // Deep dark blue/purple
        private readonly Color _accentColor = Color.Parse("#542a7b");   // Half brightness purple
        private readonly Color _accentAltColor = Color.Parse("#035b6a"); // Half brightness cyan/blue

        private bool _musicAvailable;
        private bool _musicPlaying;
        private int _musicHandle; 

        private string? _pythonPath;
        private bool _envReady;

        public MainWindow()
        {
            InitializeComponent();

            Opened += MainWindow_Opened;
            Closing += MainWindow_Closing;
        }

        private async void MainWindow_Opened(object? sender, EventArgs e)
        {
            SetControlsEnabled(false);
            StatusTextBlock.Text = "Checking environment...";
            MainProgressBar.Value = 0;
            ProgressPercentText.Text = "0%";

            InitPlasma();
            StartPlasma();

            InitMusicSystem(); 

            InitMusicToggle();

            await CheckEnvironmentAsync();

            RefreshPorts();
            SetControlsEnabled(_envReady);
            if (_envReady)
            {
                StatusTextBlock.Text = "Ready";
                MainProgressBar.Value = 0;
                ProgressPercentText.Text = "0%";
            }
        }


        private void MainWindow_Closing(object? sender, WindowClosingEventArgs e)
        {
            StopPlasma();
            StopMusic();
            FreeMusicSystem();
        }

        private void SetControlsEnabled(bool enabled)
        {
            PortCombo.IsEnabled = enabled;
            RefreshPortsButton.IsEnabled = enabled;
            BrowseFirmwareButton.IsEnabled = enabled;
            FlashButton.IsEnabled = enabled;
            EraseButton.IsEnabled = enabled;
            DumpButton.IsEnabled = enabled;
        }

        private void SetStatus(string message, double? progress = null)
        {
            Dispatcher.UIThread.Post(() =>
            {
                StatusTextBlock.Text = message;
                if (progress.HasValue)
                {
                    double p = Math.Clamp(progress.Value, 0, 100);
                    MainProgressBar.Value = p;
                    ProgressPercentText.Text = $"{(int)p}%";
                }
            });
        }

        private void InitPlasma()
        {
            _plasmaBitmap = new WriteableBitmap(
                new PixelSize(fxW, fxH),
                new Vector(96, 96),
                PixelFormat.Bgra8888,
                AlphaFormat.Opaque);
            PlasmaImage.Source = _plasmaBitmap;
            _plasmaStart = DateTime.Now;
        }

        private void StartPlasma()
        {
            if (_plasmaBitmap == null) return;

            _plasmaTimer = new DispatcherTimer(DispatcherPriority.Default)
            {
                Interval = TimeSpan.FromMilliseconds(1000 / 60.0) 
            };
            _plasmaTimer.Tick += (s, e) => RenderPlasmaFrame();
            _plasmaTimer.Start();
        }

        private void StopPlasma()
        {
            _plasmaTimer?.Stop();
            _plasmaTimer = null;
        }

        private void RenderPlasmaFrame()
        {
            if (_plasmaBitmap == null)
            {
                return;
            }

            double t = (DateTime.Now - _plasmaStart).TotalSeconds * 3.5;
            int stride = _plasmaBitmap.PixelSize.Width * 4;
            byte[] pixels = new byte[stride * _plasmaBitmap.PixelSize.Height];

            double cx = fxW / 2.0;
            double cy = fxH / 2.0;

            for (int y = 0; y < fxH; y++)
            {
                double dy = y - cy;
                for (int x = 0; x < fxW; x++)
                {
                    double dx = x - cx;

                    double v1 = Math.Sin(x / 7.0 + t);
                    double v2 = Math.Sin(y / 9.0 - t * 0.7);
                    double v3 = Math.Sin((x + y) / 11.0 + t * 0.4);
                    double v4 = Math.Sin(Math.Sqrt(dx * dx + dy * dy) / 6.0 - t * 0.9);

                    double v = (v1 + v2 + v3 + v4) / 4.0;
                    v = (v + 1.0) / 2.0;

                    Color c = PlasmaColor(v);

                    int index = y * stride + x * 4;
                    pixels[index + 0] = c.B;
                    pixels[index + 1] = c.G;
                    pixels[index + 2] = c.R;
                    pixels[index + 3] = 255;
                }
            }

            using (var lockedBitmap = _plasmaBitmap.Lock())
            {
                var size = lockedBitmap.Size;
                var address = lockedBitmap.Address;
                System.Runtime.InteropServices.Marshal.Copy(pixels, 0, address, pixels.Length);
            }
            
            if (PlasmaImage != null)
            {
                PlasmaImage.InvalidateVisual();
                PlasmaImage.InvalidateArrange();
                PlasmaImage.InvalidateMeasure();
            }
        }

        private Color Lerp(Color a, Color b, double t)
        {
            t = Math.Clamp(t, 0.0, 1.0);
            byte r = (byte)(a.R + (b.R - a.R) * t);
            byte g = (byte)(a.G + (b.G - a.G) * t);
            byte b2 = (byte)(a.B + (b.B - a.B) * t);
            return Color.FromRgb(r, g, b2);
        }

        private Color PlasmaColor(double v)
        {
            v = Math.Clamp(v, 0.0, 1.0);
            if (v < 0.5)
            {
                double t = v / 0.5;
                return Lerp(_bgColor, _accentColor, t);
            }
            else
            {
                double t = (v - 0.5) / 0.5;
                return Lerp(_accentColor, _accentAltColor, t);
            }
        }

        private void InitMusicSystem()
        {
            _musicAvailable = false;
            _musicPlaying = false;
            _musicHandle = 0;

            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                using Stream? stream = assembly.GetManifestResourceStream("Kratos_Service_utility.Resources.Electronscape - Sanxion Loader.mp3");
                if (stream == null)
                {
                    return;
                }
                using var ms = new MemoryStream();
                stream.CopyTo(ms);

                if (!Bass.Init(-1, 44100, DeviceInitFlags.Default, IntPtr.Zero))
                {
                    return;
                }

                if (!Bass.Start())
                {
                    return;
                }

                var musicData = ms.ToArray();
                _musicHandle = Bass.CreateStream(musicData, 0, musicData.Length, BassFlags.Default | BassFlags.Loop);

                _musicAvailable = _musicHandle != 0;
            }
            catch 
            {
                // do nothing
            }
        }


        private void FreeMusicSystem()
        {
            try
            {
                if (_musicHandle != 0)
                {
                    Bass.ChannelStop( _musicHandle);
                    Bass.MusicFree(_musicHandle);
                    _musicHandle = 0;
                }
                Bass.Free();
            }
            catch { }
        }

        private void InitMusicToggle()
        {
            if (_musicAvailable)
            {
                MusicCheckBox.IsEnabled = true;
                MusicCheckBox.IsChecked = true;
                StartMusic();
            }
            else
            {
                MusicCheckBox.IsEnabled = false;
                MusicCheckBox.IsChecked = false;
                StatusTextBlock.Text = "music not available – music disabled.";
            }
        }

        private void StartMusic()
        {
            if (!_musicAvailable || _musicHandle == 0)
                return;

            try
            {
                Bass.ChannelPlay(_musicHandle, false);
                _musicPlaying = true;
                StatusTextBlock.Text = "Playing soundtrack…";
            }
            catch
            {
                _musicPlaying = false;
                MusicCheckBox.IsEnabled = false;
                MusicCheckBox.IsChecked = false;
                StatusTextBlock.Text = "Music error – disabled.";
            }
        }

        private void StopMusic()
        {
            try
            {
                if (_musicHandle != 0)
                {
                    Bass.ChannelStop(_musicHandle);
                }
            }
            catch { }
            _musicPlaying = false;
        }

        private void MusicCheckBox_Click(object? sender, RoutedEventArgs e)
        {
            if (!_musicAvailable)
            {
                MusicCheckBox.IsChecked = false;
                return;
            }

            if (MusicCheckBox.IsChecked == true)
            {
                if (!_musicPlaying)
                    StartMusic();
            }
            else
            {
                StopMusic();
                StatusTextBlock.Text = "Music muted.";
            }
        }

        private async Task CheckEnvironmentAsync()
        {
            try
            {
                SetStatus("Checking for Python...", 5);
                _pythonPath = await FindPythonAsync();

                if (_pythonPath == null)
                {
                    SetStatus("Python 3 not found.", 0);

                    var result = await ShowMessageBoxAsync(
                        "Python 3 was not found on this system.\n\n" +
                        "This utility expects a system Python 3 install and uses pyserial + esptool for flashing.\n\n" +
                        "Click YES to open the official Python downloads page in your browser.",
                        "Python Not Installed",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Warning);

                    if (result == MessageBoxResult.Yes)
                    {
                        try
                        {
                            Process.Start(new ProcessStartInfo
                            {
                                FileName = "https://www.python.org/downloads/",
                                UseShellExecute = true
                            });
                        }
                        catch { }
                    }

                    _envReady = false;
                    return;
                }

                SetStatus($"Python detected at: {_pythonPath}", 10);

                SetStatus("Checking pip...", 15);
                await EnsurePipAsync(_pythonPath);

                string[] modules = { "pyserial", "esptool" };
                double step = 70.0 / modules.Length;
                double pct = 20.0;

                foreach (var m in modules)
                {
                    SetStatus($"Checking Python module: {m}...", pct);
                    await EnsureModuleAsync(_pythonPath, m);
                    pct += step;
                }

                SetStatus("Environment ready.", 100);
                _envReady = true;
            }
            catch (Exception ex)
            {
                SetStatus("Environment check failed.", 0);
                _ = ShowMessageBoxAsync(
                    $"Environment check failed:\n\n{ex}",
                    "Error",
                    MessageBoxButtons.Ok,
                    MessageBoxIcon.Error);
                _envReady = false;
            }
        }

        private static async Task<string?> FindPythonAsync()
        {
            string[] candidates = { "python", "python3" };

            foreach (var c in candidates)
            {
                try
                {
                    var result = await RunProcessCaptureAsync("where", c);
                    if (result.exitCode == 0 && !string.IsNullOrWhiteSpace(result.stdout))
                    {
                        string? line = result.stdout
                            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                            .FirstOrDefault();
                        if (!string.IsNullOrEmpty(line) && File.Exists(line))
                            return line;
                    }
                }
                catch { }
            }

            return null;
        }

        private static async Task EnsurePipAsync(string pythonPath)
        {
            var pipCheck = await RunProcessCaptureAsync(
                pythonPath, "-m pip --version");

            if (pipCheck.exitCode == 0)
                return;

            await RunProcessCaptureAsync(
                pythonPath, "-m ensurepip --default-pip");
        }

        private async Task EnsureModuleAsync(string pythonPath, string moduleName)
        {
            var showResult = await RunProcessCaptureAsync(
                pythonPath, $"-m pip show {moduleName}");

            if (showResult.exitCode == 0)
                return;

            var install = await RunProcessCaptureAsync(
                pythonPath, $"-m pip install --user {moduleName}");

            if (install.exitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Failed to install {moduleName}: {install.stderr}");
            }
        }

        private static Task<(int exitCode, string stdout, string stderr)> RunProcessCaptureAsync(
            string fileName, string arguments)
        {
            var tcs = new TaskCompletionSource<(int, string, string)>();

            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            var sbOut = new StringBuilder();
            var sbErr = new StringBuilder();

            proc.OutputDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                    sbOut.AppendLine(e.Data);
            };
            proc.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                    sbErr.AppendLine(e.Data);
            };

            proc.Exited += (_, __) =>
            {
                tcs.TrySetResult((proc.ExitCode, sbOut.ToString(), sbErr.ToString()));
                proc.Dispose();
            };

            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            return tcs.Task;
        }

        private void RefreshPortsButton_Click(object? sender, RoutedEventArgs e)
        {
            RefreshPorts();
        }

        private void RefreshPorts()
        {
            PortCombo.Items.Clear();
            var ports = SerialPort
                .GetPortNames()
                .OrderBy(p => p)
                .ToArray();

            foreach (var p in ports)
                PortCombo.Items.Add(p);

            if (ports.Length > 0)
                PortCombo.SelectedIndex = 0;
        }

        private async void BrowseFirmwareButton_Click(object? sender, RoutedEventArgs e)
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Select Firmware File",
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("Binary Firmware")
                    {
                        Patterns = ["*.bin"]
                    },
                    new FilePickerFileType("All files")
                    {
                        Patterns = ["*"]
                    }
                ]
            });

            if (files.Count > 0 && files[0] is IStorageFile file)
            {
                FirmwarePathText.Text = file.Path.LocalPath;
            }
        }

        private async void FlashButton_Click(object? sender, RoutedEventArgs e)
        {
            if (!_envReady || string.IsNullOrEmpty(_pythonPath))
            {
                _ = ShowMessageBoxAsync("Environment is not ready (Python / modules missing).", "Error",
                    MessageBoxButtons.Ok, MessageBoxIcon.Error);
                return;
            }

            if (PortCombo.SelectedItem is not string port || string.IsNullOrEmpty(port))
            {
                _ = ShowMessageBoxAsync("Select a COM port first.", "Input Missing",
                    MessageBoxButtons.Ok, MessageBoxIcon.Warning);
                return;
            }

            string fw = FirmwarePathText.Text?.Trim() ?? "";
            if (!File.Exists(fw))
            {
                _ = ShowMessageBoxAsync("Firmware file not found.", "Error",
                    MessageBoxButtons.Ok, MessageBoxIcon.Error);
                return;
            }

            SetControlsEnabled(false);
            SetStatus("Initializing flash operation...", 0);

            var args = new List<string>
            {
                "--port", port,
                "--baud", "921600",
                "--before", "default_reset",
                "--after", "hard_reset",
                "write_flash", "--erase-all",
                "--flash_mode", "dio",
                "--flash_freq", "80m",
                "--flash_size", "detect",
                "0x0", fw
            };

            try
            {
                await RunEsptoolAsync(_pythonPath!, args, "Flash");
                SetStatus("Firmware flashed successfully!", 100);
                _ = ShowMessageBoxAsync("Firmware (merged) flashed to 0x0 successfully!",
                    "Success", MessageBoxButtons.Ok, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                SetStatus("Flash operation failed.", 0);
                _ = ShowMessageBoxAsync($"Failed to flash firmware:\n\n{ex}",
                    "Flashing Failed", MessageBoxButtons.Ok, MessageBoxIcon.Error);
            }
            finally
            {
                SetStatus("Ready", 0);
                SetControlsEnabled(true);
            }
        }

        private async void EraseButton_Click(object? sender, RoutedEventArgs e)
        {
            if (!_envReady || string.IsNullOrEmpty(_pythonPath))
            {
                _ = ShowMessageBoxAsync("Environment is not ready (Python / modules missing).", "Error",
                    MessageBoxButtons.Ok, MessageBoxIcon.Error);
                return;
            }

            if (PortCombo.SelectedItem is not string port || string.IsNullOrEmpty(port))
            {
                _ = ShowMessageBoxAsync("Select a COM port first.", "Input Missing",
                    MessageBoxButtons.Ok, MessageBoxIcon.Warning);
                return;
            }

            var confirm = await ShowMessageBoxAsync(
                "This will completely erase the flash memory.\n\nAre you sure you want to continue?",
                "Confirm Erase",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);

            if (confirm != MessageBoxResult.Yes)
                return;

            SetControlsEnabled(false);
            SetStatus("Initializing erase operation...", 0);

            var args = new List<string>
            {
                "--port", port,
                "--baud", "921600",
                "erase_flash"
            };

            try
            {
                await RunEsptoolAsync(_pythonPath!, args, "Erase");
                SetStatus("Flash memory erased successfully!", 100);
                _ = ShowMessageBoxAsync("Flash memory erased successfully!",
                    "Success", MessageBoxButtons.Ok, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                SetStatus("Erase operation failed.", 0);
                _ = ShowMessageBoxAsync($"Failed to erase flash:\n\n{ex}",
                    "Erase Failed", MessageBoxButtons.Ok, MessageBoxIcon.Error);
            }
            finally
            {
                SetStatus("Ready", 0);
                SetControlsEnabled(true);
            }
        }

        private async void DumpButton_Click(object? sender, RoutedEventArgs e)
        {
            if (!_envReady || string.IsNullOrEmpty(_pythonPath))
            {
                _ = ShowMessageBoxAsync("Environment is not ready (Python / modules missing).", "Error",
                    MessageBoxButtons.Ok, MessageBoxIcon.Error);
                return;
            }

            if (PortCombo.SelectedItem is not string port || string.IsNullOrEmpty(port))
            {
                _ = ShowMessageBoxAsync("Select a COM port first.", "Input Missing",
                    MessageBoxButtons.Ok, MessageBoxIcon.Warning);
                return;
            }

            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save Firmware Dump As",
                DefaultExtension = "bin",
                SuggestedFileName = "flash_dump.bin",
                FileTypeChoices = new[]
                {
                    new FilePickerFileType("Binary files")
                    {
                        Patterns = new[] { "*.bin" }
                    },
                    new FilePickerFileType("All files")
                    {
                        Patterns = new[] { "*" }
                    }
                }
            });

            if (file == null)
                return;

            string savePath = file.Path.LocalPath;

            SetControlsEnabled(false);
            SetStatus("Initializing dump operation...", 0);

            const int flashSize = 0x400000; // 4MB default

            var args = new List<string>
            {
                "--port", port,
                "--baud", "921600",
                "read_flash",
                "0x0",
                $"0x{flashSize:x}",
                savePath
            };

            try
            {
                await RunEsptoolAsync(_pythonPath!, args, "Dump");
                SetStatus("Firmware dumped successfully!", 100);
                _ = ShowMessageBoxAsync($"Firmware dumped successfully to:\n{savePath}",
                    "Success", MessageBoxButtons.Ok, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                SetStatus("Dump operation failed.", 0);
                _ = ShowMessageBoxAsync($"Failed to dump firmware:\n\n{ex}",
                    "Dump Failed", MessageBoxButtons.Ok, MessageBoxIcon.Error);
            }
            finally
            {
                SetStatus("Ready", 0);
                SetControlsEnabled(true);
            }
        }

        private async Task RunEsptoolAsync(string pythonPath, List<string> args, string action)
        {
            var psi = new ProcessStartInfo
            {
                FileName = pythonPath,
                Arguments = $"-u -m esptool {BuildArgumentString(args)}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            proc.Start();

            var readOutTask = Task.Run(async () =>
            {
                while (!proc.HasExited)
                {
                    string? line = await proc.StandardOutput.ReadLineAsync();
                    if (line == null) break;
                    HandleEsptoolLine(line, action);
                }

                string? rest;
                while ((rest = await proc.StandardOutput.ReadLineAsync()) != null)
                {
                    HandleEsptoolLine(rest, action);
                }
            });

            var readErrTask = Task.Run(async () =>
            {
                while (!proc.HasExited)
                {
                    string? line = await proc.StandardError.ReadLineAsync();
                    if (line == null) break;
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        // optional: log stderr
                    }
                }
            });

            await Task.WhenAll(readOutTask, readErrTask);
            proc.WaitForExit();

            if (proc.ExitCode != 0)
            {
                throw new InvalidOperationException($"esptool exited with code {proc.ExitCode}");
            }
        }

        private static string BuildArgumentString(IEnumerable<string> args)
        {
            return string.Join(" ", args.Select(a =>
                string.IsNullOrEmpty(a) ? "" :
                a.Contains(' ') ? $"\"{a}\"" : a));
        }

        private void HandleEsptoolLine(string line, string action)
        {
            line = line.Trim();
            if (string.IsNullOrEmpty(line)) return;

            if (line.Contains('%'))
            {
                string[] patterns = { @"\((\d+)\s*%\)", @"(\d+)%", @"(\d+)\s*%" };
                foreach (var pat in patterns)
                {
                    var m = Regex.Match(line, pat);
                    if (m.Success && int.TryParse(m.Groups[1].Value, out int pct))
                    {
                        SetStatus($"{action} in progress... {pct}%", pct);
                        return;
                    }
                }
            }

            if (action == "Flash")
            {
                if (line.Contains("Chip is"))
                    SetStatus("Detected ESP32 chip, preparing flash...", 5);
                else if (line.Contains("Changing baud rate"))
                    SetStatus("Configuring communication speed...", 10);
                else if (line.Contains("Erasing flash"))
                    SetStatus("Erasing flash memory...", 20);
                else if (line.Contains("Writing at"))
                {
                    var match = Regex.Match(line, @"0x([0-9a-fA-F]+)");
                    if (match.Success)
                    {
                        int addr = Convert.ToInt32(match.Groups[1].Value, 16);
                        double progress = Math.Min(30 + (addr / 0x400000d) * 60, 90);
                        SetStatus($"Writing firmware data at 0x{addr:X8}...", progress);
                    }
                }
                else if (line.Contains("Hash of data verified"))
                    SetStatus("Verifying written data...", 95);
                else if (line.Contains("Leaving") || line.Contains("Hard resetting"))
                    SetStatus("Finalizing flash operation...", 98);
            }
            else if (action == "Erase")
            {
                if (line.Contains("Chip is"))
                    SetStatus("Detected ESP32 chip...", 10);
                else if (line.Contains("Erasing flash"))
                    SetStatus("Erasing flash memory...", 50);
                else if (line.Contains("Chip erase completed"))
                    SetStatus("Flash erase completed...", 90);
                else if (line.Contains("Hard resetting"))
                    SetStatus("Finalizing erase operation...", 95);
            }
            else if (action == "Dump")
            {
                if (line.Contains("Chip is"))
                    SetStatus("Detected ESP32 chip...", 5);
                else if (line.Contains("Reading flash"))
                    SetStatus("Reading flash memory...", 20);
                else if (line.Contains("Read") && line.Contains("bytes"))
                {
                    var m = Regex.Match(line, @"(\d+)\s+bytes");
                    if (m.Success && int.TryParse(m.Groups[1].Value, out int bytesRead))
                    {
                        double progress = Math.Min(20 + (bytesRead / 0x400000d) * 70, 90);
                        SetStatus($"Reading firmware data... {bytesRead:N0} bytes", progress);
                    }
                }
            }
        }

        private async Task<MessageBoxResult> ShowMessageBoxAsync(
            string message, string title, MessageBoxButtons buttons, MessageBoxIcon icon)
        {
            return await MessageBox.Show(this, message, title, buttons, icon);
        }
    }
}

