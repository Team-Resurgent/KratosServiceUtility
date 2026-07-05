using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using EL;
using ManagedBass;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Ports;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
//using EspDotNet;
//using EspDotNet.Tools;
//using EspDotNet.Tools.Firmware;
//using EspDotNet.Communication;

namespace KratosServiceUtility
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
                using Stream? stream = assembly.GetManifestResourceStream("KratosServiceUtility.Resources.Electronscape - Sanxion Loader.mp3");
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
                SetStatus("Checking environment...", 10);
                await Task.Delay(100);
                
                var ports = SerialPort.GetPortNames();
                if (ports.Length == 0)
                {
                    SetStatus("No serial ports detected.", 50);
                }
                else
                {
                    SetStatus($"Found {ports.Length} serial port(s).", 50);
                }

                SetStatus("Environment ready.", 100);
                _envReady = true;
            }
            catch (Exception ex)
            {
                SetStatus("Environment check failed.", 0);
                _ = ShowMessageBoxAsync(
                    $"Environment check failed.",
                    "Error",
                    MessageBoxButtons.Ok,
                    MessageBoxIcon.Error);
                _envReady = false;
            }
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

        internal class FlashProgress : IProgress<int>
        {
            int _old = -1;

            private MainWindow _owner;

            public string Message { get; set; } = string.Empty;

            public FlashProgress(MainWindow owner)
            {
                _owner = owner;
            }


            public void Report(int value)
            {
                _owner.SetStatus(string.Format(Message, value), value);
            }
        }

        private async void FlashButton_Click(object? sender, RoutedEventArgs e)
        {
            if (!_envReady)
            {
                _ = ShowMessageBoxAsync("Environment is not ready.", "Error",
                    MessageBoxButtons.Ok, MessageBoxIcon.Error);
                return;
            }

            if (PortCombo.SelectedItem is not string port || string.IsNullOrEmpty(port))
            {
                _ = ShowMessageBoxAsync("Select a serial port first.", "Input Missing",
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

            try
            {
                await Task.Run(async () =>
                {
                    var flashProgress = new FlashProgress(this);

                    using var link = new EspLink(port, EspSerialType.UsbSerialJtag);

                    link.SerialHandshake = Handshake.RequestToSend;
                    flashProgress.Message = "Connecting...";
                    await link.ConnectAsync(EspConnectMode.Default, 3, false, default, link.DefaultTimeout);

                    flashProgress.Message = "Running stub... {0}%";
                    await link.RunStubAsync(default, link.DefaultTimeout, flashProgress);
                    await link.SetBaudRateAsync(921600, default, link.DefaultTimeout);

                    using FileStream stm = File.Open(fw, FileMode.Open, FileAccess.Read);
                    flashProgress.Message = "Writing firmware... {0}%";
                    await link.FlashAsync(default, stm, true, 16384, 0x10000, 3, false, link.DefaultTimeout, flashProgress);
                    await link.ResetAsync(default);
                });

                SetStatus("Firmware flashed successfully!", 100);
                _ = ShowMessageBoxAsync("Firmware flashed successfully!",
                    "Success", MessageBoxButtons.Ok, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                SetStatus("Flash operation failed.", 0);
                LogError("Flash", ex);
                _ = ShowMessageBoxAsync($"Failed to flash firmware.\n\n{ex}",
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
            if (!_envReady)
            {
                _ = ShowMessageBoxAsync("Environment is not ready.", "Error",
                    MessageBoxButtons.Ok, MessageBoxIcon.Error);
                return;
            }

            if (PortCombo.SelectedItem is not string port || string.IsNullOrEmpty(port))
            {
                _ = ShowMessageBoxAsync("Select a serial port first.", "Input Missing",
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

            try
            {
                await Task.Run(async () =>
                {
                    var flashProgress = new FlashProgress(this);

                    using var link = new EspLink(port, EspSerialType.UsbSerialJtag);

                    link.SerialHandshake = Handshake.RequestToSend;
                    flashProgress.Message = "Connecting...";
                    await link.ConnectAsync(EspConnectMode.Default, 3, false, default, link.DefaultTimeout);

                    flashProgress.Message = "Running stub... {0}%";
                    await link.RunStubAsync(default, link.DefaultTimeout, flashProgress);
                    await link.SetBaudRateAsync(921600, default, link.DefaultTimeout);

                    // todo: erase code

                    await link.ResetAsync(default);
                });

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
            if (!_envReady)
            {
                _ = ShowMessageBoxAsync("Environment is not ready.", "Error",
                    MessageBoxButtons.Ok, MessageBoxIcon.Error);
                return;
            }

            if (PortCombo.SelectedItem is not string port || string.IsNullOrEmpty(port))
            {
                _ = ShowMessageBoxAsync("Select a serial port first.", "Input Missing",
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

            const int flashSize = 0x400000;

            try
            {
                await Task.Run(async () =>
                {
                    var flashProgress = new FlashProgress(this);

                    using var link = new EspLink(port, EspSerialType.UsbSerialJtag);

                    link.SerialHandshake = Handshake.RequestToSend;
                    flashProgress.Message = "Connecting...";
                    await link.ConnectAsync(EspConnectMode.Default, 3, false, default, link.DefaultTimeout);

                    flashProgress.Message = "Running stub... {0}%";
                    await link.RunStubAsync(default, link.DefaultTimeout, flashProgress);
                    await link.SetBaudRateAsync(921600, default, link.DefaultTimeout);

                    // todo: dump code savePath

                    await link.ResetAsync(default);
                });

                SetStatus("Firmware dumped successfully!", 100);
                _ = ShowMessageBoxAsync($"Firmware dumped successfully to:\n{savePath}",
                    "Success", MessageBoxButtons.Ok, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                SetStatus("Dump operation failed.", 0);
                LogError("Dump", ex);
                _ = ShowMessageBoxAsync($"Failed to dump firmware.\n\n{ex}",
                    "Dump Failed", MessageBoxButtons.Ok, MessageBoxIcon.Error);
            }
            finally
            {
                SetStatus("Ready", 0);
                SetControlsEnabled(true);
            }
        }


        private static void LogError(string op, Exception ex)
        {
            try
            {
                string path = Path.Combine(AppContext.BaseDirectory, "kratos-flash.log");
                File.AppendAllText(path,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {op} failed:\n{ex}\n\n");
            }
            catch { }
        }

        private async Task<MessageBoxResult> ShowMessageBoxAsync(
            string message, string title, MessageBoxButtons buttons, MessageBoxIcon icon)
        {
            return await MessageBox.Show(this, message, title, buttons, icon);
        }
    }
}

