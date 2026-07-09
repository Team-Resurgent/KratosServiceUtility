using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Ports;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace KratosServiceUtility
{
    /// <summary>
    /// A lightweight serial monitor. Owns its own <see cref="SerialPort"/> (separate from EspLink's), so
    /// the main window must release the port to us only when it isn't flashing. It streams the device's
    /// console output — handy for watching the boot log immediately after a flash.
    /// </summary>
    public partial class SerialMonitorWindow : Window
    {
        // ESP-IDF console baud. USB-Serial-JTAG ignores the rate, but a UART bridge (CP2102 etc.) needs it.
        private const int ConsoleBaud = 115200;
        // Cap the on-screen log so a long-running session can't grow memory without bound.
        private const int MaxChars = 200_000;

        // Strip ANSI colour/cursor escapes so IDF's coloured logs don't render as garbage.
        private static readonly Regex AnsiEscape =
            new Regex(((char)27) + "\\[[0-9;?]*[ -/]*[@-~]", RegexOptions.Compiled);

        private readonly object _bufLock = new();
        private readonly List<byte> _rxBytes = new();
        private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
        private readonly DispatcherTimer _flushTimer;

        private SerialPort? _port;
        private string _portName = "";
        private volatile bool _closing;

        public SerialMonitorWindow()
        {
            InitializeComponent();

            // Batch UI updates: DataReceived accumulates bytes on a pool thread; this timer flushes them
            // to the text box on the UI thread a few times a second so we don't thrash the layout.
            _flushTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(50)
            };
            _flushTimer.Tick += (_, _) => FlushToUi();

            Closing += (_, _) => { _closing = true; StopMonitoring(); };
        }

        /// <summary>The port this monitor last targeted.</summary>
        public string PortName => _portName;

        /// <summary>True while a port is open and streaming.</summary>
        public bool IsMonitoring => _port != null && _port.IsOpen;

        /// <summary>
        /// Open <paramref name="portName"/> and start streaming. Safe to call repeatedly — it closes any
        /// previous port first. The open runs on a background task with retries because the ESP32-S3's
        /// native USB CDC re-enumerates on reset (right after a flash), so the port is briefly absent.
        /// </summary>
        public void StartMonitoring(string portName)
        {
            StopMonitoring();
            _portName = portName;
            _closing = false;
            SetInfo($"Connecting to {portName} ...");
            _flushTimer.Start();
            _ = Task.Run(() => OpenWithRetry(portName));
        }

        private void OpenWithRetry(string portName)
        {
            const int maxAttempts = 40; // ~10 s at 250 ms — covers the USB re-enumeration window
            for (int attempt = 1; !_closing; attempt++)
            {
                SerialPort? port = null;
                try
                {
                    port = new SerialPort(portName, ConsoleBaud, Parity.None, 8, StopBits.One)
                    {
                        ReadTimeout = 500,
                        WriteTimeout = 500,
                        // Leave the auto-reset lines released; we only want to listen, not reboot the board.
                        DtrEnable = false,
                        RtsEnable = false
                    };
                    port.DataReceived += Port_DataReceived;
                    port.ErrorReceived += (_, __) => { };
                    port.Open();
                    _port = port;
                    Dispatcher.UIThread.Post(() => SetInfo($"Connected: {portName} @ {ConsoleBaud}"));
                    return;
                }
                catch (Exception ex) when (attempt < maxAttempts &&
                                           (ex is UnauthorizedAccessException || ex is IOException ||
                                            ex is FileNotFoundException || ex is ArgumentException))
                {
                    try { port?.Dispose(); } catch { }
                    Thread.Sleep(250);
                }
                catch (Exception ex)
                {
                    try { port?.Dispose(); } catch { }
                    Dispatcher.UIThread.Post(() => SetInfo($"Could not open {portName}: {ex.Message}"));
                    return;
                }
            }
        }

        /// <summary>Close the port and stop streaming. Safe to call when not monitoring.</summary>
        public void StopMonitoring()
        {
            _flushTimer.Stop();
            var port = _port;
            _port = null;
            if (port != null)
            {
                try { port.DataReceived -= Port_DataReceived; } catch { }
                try { if (port.IsOpen) port.Close(); } catch { }
                try { port.Dispose(); } catch { }
            }
            lock (_bufLock) { _rxBytes.Clear(); }
            if (!_closing) SetInfo("Disconnected");
        }

        private void Port_DataReceived(object? sender, SerialDataReceivedEventArgs e)
        {
            var port = _port;
            if (port == null || !port.IsOpen) return;
            try
            {
                int n = port.BytesToRead;
                if (n <= 0) return;
                var buf = new byte[n];
                int read = port.Read(buf, 0, n);
                lock (_bufLock)
                {
                    for (int i = 0; i < read; i++) _rxBytes.Add(buf[i]);
                }
            }
            catch { /* port dropped mid-read (reset/unplug); nothing to do here */ }
        }

        private void FlushToUi()
        {
            byte[] bytes;
            lock (_bufLock)
            {
                if (_rxBytes.Count == 0) return;
                bytes = _rxBytes.ToArray();
                _rxBytes.Clear();
            }

            // Stateful UTF-8 decode so a multibyte char split across two reads isn't corrupted. UTF-8
            // never yields more chars than bytes, so the byte count is a safe buffer size.
            var chars = new char[bytes.Length];
            int produced = _decoder.GetChars(bytes, 0, bytes.Length, chars, 0, flush: false);
            if (produced == 0) return;

            var text = AnsiEscape.Replace(new string(chars, 0, produced), "");
            if (text.Length == 0) return;

            AppendText(text);
        }

        private void AppendText(string text)
        {
            var combined = (LogText.Text ?? "") + text;
            if (combined.Length > MaxChars)
            {
                combined = combined.Substring(combined.Length - MaxChars);
            }
            LogText.Text = combined;

            if (AutoScrollCheck.IsChecked == true)
            {
                LogScroll.ScrollToEnd();
            }
        }

        private void SetInfo(string s) => InfoText.Text = s;

        private void ClearButton_Click(object? sender, RoutedEventArgs e) => LogText.Text = "";

        private void ReconnectButton_Click(object? sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(_portName)) StartMonitoring(_portName);
        }
    }
}
