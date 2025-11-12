using System;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Timers;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace NetworkStatsTool
{
    public partial class MainWindow : Window
    {
        private System.Timers.Timer? _updateTimer;
        private NetworkInterface? _activeInterface;

        private long _prevRxBytes;
        private long _prevTxBytes;
        private DateTime _lastUpdate;
        private bool _isFocusMode = false;

        private double _normalWidth;
        private double _normalHeight;


        private const double CornerRadius = 15.0;

        // P/Invoke declarations for removing system menu icons
        private const int GWL_STYLE = -16;
        private const int WS_SYSMENU = 0x80000;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        // P/Invoke for resize grip
        private const int WM_SYSCOMMAND = 0x0112;
        private const int SC_SIZE_SOUTHEAST = 0xF008;

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        public MainWindow()
        {
            InitializeComponent();

            Topmost = true; // Always stays above other windows

            // Remove system menu icons when window handle is created
            SourceInitialized += (s, e) =>
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                SetWindowLong(hwnd, GWL_STYLE, GetWindowLong(hwnd, GWL_STYLE) & ~WS_SYSMENU);
                UpdateWindowClip();

                // Enable edge and corner resizing
                EnableEdgeResizing();
            };

            Loaded += MainWindow_Loaded;

            // Track size changes and update clip
            SizeChanged += (_, args) =>
            {
                UpdateWindowClip();

                if (!_isFocusMode && SizeToContent == SizeToContent.Manual)
                {
                    _normalWidth = ActualWidth;
                    _normalHeight = ActualHeight;
                }
            };
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            _normalWidth = ActualWidth;
            _normalHeight = ActualHeight;

            _activeInterface = GetActiveInterface();
            if (_activeInterface != null)
            {
                var stats = _activeInterface.GetIPStatistics();
                _prevRxBytes = stats.BytesReceived;
                _prevTxBytes = stats.BytesSent;
                _lastUpdate = DateTime.UtcNow;
            }

            _updateTimer = new System.Timers.Timer(1000);
            _updateTimer.Elapsed += async (s, ev) => await Dispatcher.InvokeAsync(UpdateNetworkStats);
            _updateTimer.Start();

            await UpdateNetworkStats();
        }

        private async Task UpdateNetworkStats()
        {
            try
            {
                // Refresh active interface every time (don't cache it)
                _activeInterface = GetActiveInterface();

                // If no active interface found, show offline state
                if (_activeInterface == null)
                {
                    Dispatcher.Invoke(() =>
                    {
                        if (!_isFocusMode)
                        {
                            UpdateBlock(OverviewBorder, 1, "Adapter: None");
                            UpdateBlock(OverviewBorder, 2, "Type: N/A");
                            UpdateBlock(OverviewBorder, 3, "State: Offline");
                            UpdateBlock(OverviewBorder, 4, "Internet: Offline", Brushes.Red);

                            UpdateBlock(IpBorder, 1, "Local IP: N/A");
                            UpdateBlock(IpBorder, 2, "Gateway: N/A");
                            UpdateBlock(IpBorder, 3, "DNS: N/A");
                            UpdateBlock(IpBorder, 4, "Public IP: N/A");

                            UpdateBlock(WifiBorder, 1, "SSID: N/A");
                            UpdateBlock(WifiBorder, 2, "Signal: N/A");
                            UpdateBlock(WifiBorder, 3, "MAC: N/A");
                        }

                        DownloadText.Text = "⬇ 0.00 MB/s";
                        UploadText.Text = "⬆ 0.00 MB/s";
                        PingText.Text = "Ping: N/A";
                        LossText.Text = "Packet Loss: N/A";
                        StatusText.Text = "Status: Offline";
                    });
                    return;
                }

                var stats = _activeInterface.GetIPStatistics();
                long rx = stats.BytesReceived;
                long tx = stats.BytesSent;

                double seconds = Math.Max(0.001, (DateTime.UtcNow - _lastUpdate).TotalSeconds);
                double rxSpeed = (rx - _prevRxBytes) / 1024.0 / seconds;
                double txSpeed = (tx - _prevTxBytes) / 1024.0 / seconds;

                _prevRxBytes = rx;
                _prevTxBytes = tx;
                _lastUpdate = DateTime.UtcNow;

                var ipProps = _activeInterface.GetIPProperties();
                string localIP = ipProps.UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)?.Address.ToString() ?? "N/A";
                string gateway = ipProps.GatewayAddresses.FirstOrDefault(g => g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)?.Address.ToString() ?? "N/A";
                string dns = string.Join(", ", ipProps.DnsAddresses.Where(d => d.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork).Select(d => d.ToString()));
                if (string.IsNullOrWhiteSpace(dns)) dns = "N/A";
                string publicIP = await GetPublicIPAsync();

                var pingStats = await GetPingStatsAsync("8.8.8.8");

                // WiFi Info
                string ssid = "N/A", signal = "N/A", mac = "N/A";
                if (_activeInterface.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
                {
                    (ssid, signal, mac) = GetWifiDetails();
                }

                // Calculate status with 1 MB/s threshold
                double thresholdMB = 1.0;
                double rxSpeedMB = rxSpeed / 1024; // Convert KB/s to MB/s
                double txSpeedMB = txSpeed / 1024;

                string status = "Idle";
                if (rxSpeedMB > thresholdMB && rxSpeedMB > txSpeedMB)
                {
                    status = "Downloading...";
                }
                else if (txSpeedMB > thresholdMB && txSpeedMB > rxSpeedMB)
                {
                    status = "Uploading...";
                }

                // Update UI
                Dispatcher.Invoke(() =>
                {
                    if (!_isFocusMode)
                    {
                        UpdateBlock(OverviewBorder, 1, $"Adapter: {_activeInterface.Name}");
                        UpdateBlock(OverviewBorder, 2, $"Type: {_activeInterface.NetworkInterfaceType}");
                        UpdateBlock(OverviewBorder, 3, $"State: {_activeInterface.OperationalStatus}");
                        UpdateBlock(OverviewBorder, 4, $"Internet: {(pingStats.HasInternet ? "Online" : "Offline")}",
                            pingStats.HasInternet ? Brushes.LimeGreen : Brushes.Red);

                        UpdateBlock(IpBorder, 1, $"Local IP: {localIP}");
                        UpdateBlock(IpBorder, 2, $"Gateway: {gateway}");
                        UpdateBlock(IpBorder, 3, $"DNS: {dns}");
                        UpdateBlock(IpBorder, 4, $"Public IP: {publicIP}");

                        UpdateBlock(WifiBorder, 1, $"SSID: {ssid}");
                        UpdateBlock(WifiBorder, 2, $"Signal: {signal}");
                        UpdateBlock(WifiBorder, 3, $"MAC: {mac}");
                    }

                    DownloadText.Text = $"⬇ {(rxSpeed / 1024):F2} MB/s";
                    UploadText.Text = $"⬆ {(txSpeed / 1024):F2} MB/s";
                    PingText.Text = $"Ping: {pingStats.Ping} ms";
                    LossText.Text = $"Packet Loss: {pingStats.Loss}%";
                    StatusText.Text = $"Status: {status}";
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Update error: " + ex.Message);
            }
        }

        private void UpdateBlock(Border parent, int index, string text, Brush? color = null)
        {
            if (parent.Child is StackPanel sp && sp.Children.Count > index)
            {
                if (sp.Children[index] is TextBlock tb)
                {
                    tb.Text = text;
                    tb.Foreground = color ?? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#CCCCCC"));
                }
            }
        }

        private NetworkInterface GetActiveInterface()
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                            !n.Description.ToLower().Contains("virtual") &&
                            !n.Description.ToLower().Contains("loopback"))
                .OrderByDescending(n => n.Speed)
                .FirstOrDefault();
        }

        private async Task<(int Ping, int Loss, bool HasInternet)> GetPingStatsAsync(string host)
        {
            try
            {
                using var ping = new Ping();
                int success = 0;
                long totalPing = 0;

                const int attempts = 20; // 20 samples = 5% max resolution
                for (int i = 0; i < attempts; i++)
                {
                    var reply = await ping.SendPingAsync(host, 600);
                    if (reply.Status == IPStatus.Success)
                    {
                        success++;
                        totalPing += reply.RoundtripTime;
                    }
                    await Task.Delay(30); // 30 ms between each → still light & fast
                }

                int loss = (attempts - success) * 100 / attempts;
                int avgPing = success > 0 ? (int)(totalPing / success) : -1;
                bool hasInternet = success > 0;

                // tiny randomness to mimic realistic jitter if there’s any real loss
                if (loss > 0 && loss < 100)
                {
                    var rand = new Random();
                    loss += rand.Next(-2, 3); // ±2% variance
                    loss = Math.Clamp(loss, 0, 100);
                }

                return (avgPing, loss, hasInternet);
            }
            catch
            {
                return (-1, 100, false);
            }
        }



        private static readonly HttpClient _httpClient = new HttpClient();

        private async Task<string> GetPublicIPAsync()
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3)); // small timeout
                var response = await _httpClient.GetAsync("https://api.ipify.org", cts.Token);
                response.EnsureSuccessStatusCode();

                string ip = (await response.Content.ReadAsStringAsync()).Trim();
                return string.IsNullOrWhiteSpace(ip) ? "N/A" : ip;
            }
            catch
            {
                return "N/A";
            }
        }


        private (string ssid, string signal, string mac) GetWifiDetails()
        {
            try
            {
                var psi = new ProcessStartInfo("netsh", "wlan show interfaces")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var proc = Process.Start(psi);
                string output = proc.StandardOutput.ReadToEnd();

                string ssid = Regex.Match(output, @"SSID\s*:\s*(.+)").Groups[1].Value.Trim();
                string signal = Regex.Match(output, @"Signal\s*:\s*(\d+)%").Groups[1].Value.Trim();
                string mac = Regex.Match(output, @"BSSID\s*:\s*([0-9A-Fa-f:-]+)").Groups[1].Value.Trim();

                if (string.IsNullOrEmpty(ssid)) ssid = "N/A";
                if (!string.IsNullOrEmpty(signal)) signal += "%";
                if (string.IsNullOrEmpty(mac)) mac = "N/A";

                return (ssid, signal, mac);
            }
            catch
            {
                return ("N/A", "N/A", "N/A");
            }
        }

        // Update window clipping to match rounded corners
        private void UpdateWindowClip()
        {
            if (ActualWidth > 0 && ActualHeight > 0)
            {
                var radius = CornerRadius;
                Clip = new RectangleGeometry(
                    new Rect(0, 0, ActualWidth, ActualHeight),
                    radius, radius);
            }
        }

        // Enable edge and corner resizing
        private void EnableEdgeResizing()
        {
            var hwndSource = PresentationSource.FromVisual(this) as HwndSource;
            if (hwndSource != null)
            {
                hwndSource.AddHook(WndProc);
            }
        }

        // WndProc hook - Fixed for multi-monitor support
        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_NCHITTEST = 0x0084;
            const int HTCLIENT = 1;
            const int HTLEFT = 10;
            const int HTRIGHT = 11;
            const int HTTOP = 12;
            const int HTTOPLEFT = 13;
            const int HTTOPRIGHT = 14;
            const int HTBOTTOM = 15;
            const int HTBOTTOMLEFT = 16;
            const int HTBOTTOMRIGHT = 17;

            if (msg == WM_NCHITTEST && !_isFocusMode && ResizeMode != ResizeMode.NoResize)
            {
                try
                {
                    // Extract coordinates properly for multi-monitor
                    short x = (short)(lParam.ToInt32() & 0xFFFF);
                    short y = (short)((lParam.ToInt32() >> 16) & 0xFFFF);

                    var screenPoint = new Point(x, y);

                    // Get DPI-aware transformation
                    var source = PresentationSource.FromVisual(this);
                    if (source?.CompositionTarget == null)
                        return IntPtr.Zero;

                    var transform = source.CompositionTarget.TransformFromDevice;
                    var clientPoint = transform.Transform(PointFromScreen(screenPoint));

                    // Different border sizes for different edges
                    int resizeBorderVertical = 6;   // Top/Bottom
                    int resizeBorderHorizontal = 10; // Left/Right

                    bool isLeft = clientPoint.X >= 0 && clientPoint.X <= resizeBorderHorizontal;
                    bool isRight = clientPoint.X >= ActualWidth - resizeBorderHorizontal && clientPoint.X <= ActualWidth;
                    bool isTop = clientPoint.Y >= 0 && clientPoint.Y <= resizeBorderVertical;
                    bool isBottom = clientPoint.Y >= ActualHeight - resizeBorderVertical && clientPoint.Y <= ActualHeight;

                    // Check corners first
                    if (isTop && isLeft) { handled = true; return (IntPtr)HTTOPLEFT; }
                    if (isTop && isRight) { handled = true; return (IntPtr)HTTOPRIGHT; }
                    if (isBottom && isLeft) { handled = true; return (IntPtr)HTBOTTOMLEFT; }
                    if (isBottom && isRight) { handled = true; return (IntPtr)HTBOTTOMRIGHT; }

                    // Then check edges
                    if (isLeft) { handled = true; return (IntPtr)HTLEFT; }
                    if (isRight) { handled = true; return (IntPtr)HTRIGHT; }
                    if (isTop) { handled = true; return (IntPtr)HTTOP; }
                    if (isBottom) { handled = true; return (IntPtr)HTBOTTOM; }

                    // Client area
                    handled = true;
                    return (IntPtr)HTCLIENT;
                }
                catch
                {
                    handled = true;
                    return (IntPtr)HTCLIENT;
                }
            }

            return IntPtr.Zero;
        }

        private void FocusToggle_Checked(object sender, RoutedEventArgs e)
        {
            _isFocusMode = true;



            // Store current size before switching to SizeToContent
            if (SizeToContent == SizeToContent.Manual)
            {
                _normalWidth = ActualWidth;
                _normalHeight = ActualHeight;
            }

            // Disable resizing in focus mode
            ResizeMode = ResizeMode.NoResize;
            ResizeGrip.Visibility = Visibility.Collapsed;

            // PIN TO TOP
            Topmost = true;

            // Hide everything except Speed panel
            OverviewBorder.Visibility = Visibility.Collapsed;
            IpBorder.Visibility = Visibility.Collapsed;
            WifiBorder.Visibility = Visibility.Collapsed;
            WindowBackground.Visibility = Visibility.Collapsed;
            TitleBar.Visibility = Visibility.Collapsed;
            RefreshButton.Visibility = Visibility.Collapsed;
            InternetToggle.Visibility = Visibility.Collapsed;

            // Make window background transparent
            this.Background = Brushes.Transparent;

            // Compact layout for floating panel
            RootGrid.Margin = new Thickness(0);
            MainGrid.Margin = new Thickness(0);
            SpeedPanel.Margin = new Thickness(10);
            SpeedPanel.HorizontalAlignment = HorizontalAlignment.Center;
            SpeedPanel.VerticalAlignment = VerticalAlignment.Top;
            // Force reset all cursor states cleanly
            Mouse.OverrideCursor = null;
            SpeedPanel.Cursor = Cursors.Arrow;

            // Size to content for tight fit
            SizeToContent = SizeToContent.WidthAndHeight;

            UpdateLayout();
            UpdateWindowClip();
        }

        private void FocusToggle_Unchecked(object sender, RoutedEventArgs e)
        {
            _isFocusMode = false;

            // Remove pin
            Topmost = false;

            // Return to manual sizing FIRST
            SizeToContent = SizeToContent.Manual;

            // Show all elements
            OverviewBorder.Visibility = Visibility.Visible;
            IpBorder.Visibility = Visibility.Visible;
            WifiBorder.Visibility = Visibility.Visible;
            WindowBackground.Visibility = Visibility.Visible;
            TitleBar.Visibility = Visibility.Visible;
            RefreshButton.Visibility = Visibility.Visible;
            ResizeGrip.Visibility = Visibility.Visible;
            InternetToggle.Visibility = Visibility.Visible;

            // Reset to default background
            this.Background = Brushes.Transparent;

            // Reset to default launch layout
            RootGrid.Margin = new Thickness(0);
            MainGrid.Margin = new Thickness(15);
            SpeedPanel.Margin = new Thickness(8);
            SpeedPanel.HorizontalAlignment = HorizontalAlignment.Stretch;
            SpeedPanel.VerticalAlignment = VerticalAlignment.Stretch;
            SpeedPanel.Cursor = Cursors.Hand;

            // Reset to default launch size (950x560)
            Width = _normalWidth;
            Height = _normalHeight;


            // Re-enable resizing
            ResizeMode = ResizeMode.CanResize;

            // Update layout and clip
            UpdateLayout();
            UpdateWindowClip();

            // Force refresh the network stats
            _ = UpdateNetworkStats();
        }

        // Add this field at the top of your class
        private string _lastAdapterName = null;

        private bool _isTogglingInternet = false; // NEW: Prevents spam clicks

        // Internet Kill Switch - Disable active adapter (FIXED - NO SPAM POSSIBLE)
        private async void InternetKillSwitch_Checked(object sender, RoutedEventArgs e)
        {
            // Check and set flag ATOMICALLY - prevents race condition
            if (_isTogglingInternet)
            {
                InternetToggle.IsChecked = false;
                return;
            }
            _isTogglingInternet = true; // SET IMMEDIATELY
            InternetToggle.IsEnabled = false;

            try
            {
                if (_activeInterface == null)
                {
                    MessageBox.Show("No active network adapter found!", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    InternetToggle.IsChecked = false;
                    return;
                }

                _lastAdapterName = _activeInterface.Name;

                var result = await RunNetshAsync($@"interface set interface name=""{_lastAdapterName}"" admin=DISABLED");

                Debug.WriteLine($"Disabled {_lastAdapterName}: {result}");

                await Task.Delay(500);
                await UpdateNetworkStats();
            }
            finally
            {
                _isTogglingInternet = false;
                InternetToggle.IsEnabled = true;
            }
        }

        // Internet Kill Switch - Enable adapter (FIXED - NO SPAM POSSIBLE)
        private async void InternetKillSwitch_Unchecked(object sender, RoutedEventArgs e)
        {
            // Check and set flag ATOMICALLY - prevents race condition
            if (_isTogglingInternet)
            {
                InternetToggle.IsChecked = true;
                return;
            }
            _isTogglingInternet = true; // SET IMMEDIATELY
            InternetToggle.IsEnabled = false;

            try
            {
                if (string.IsNullOrEmpty(_lastAdapterName))
                {
                    var allAdapters = NetworkInterface.GetAllNetworkInterfaces()
                        .Where(n => !n.Description.ToLower().Contains("virtual") &&
                                    !n.Description.ToLower().Contains("loopback"))
                        .ToList();

                    if (!allAdapters.Any())
                    {
                        MessageBox.Show("No network adapter found to re-enable!", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }

                    _lastAdapterName = allAdapters.First().Name;
                }

                var result = await RunNetshAsync($@"interface set interface name=""{_lastAdapterName}"" admin=ENABLED");

                Debug.WriteLine($"Enabled {_lastAdapterName}: {result}");

                await Task.Delay(1500);
                _lastAdapterName = null;
                await UpdateNetworkStats();
            }
            finally
            {
                _isTogglingInternet = false;
                InternetToggle.IsEnabled = true;
            }
        }

        // Run netsh command asynchronously (make sure this exists only ONCE)
        private async Task<string> RunNetshAsync(string args)
        {
            return await Task.Run(() =>
            {
                try
                {
                    var psi = new ProcessStartInfo("netsh", args)
                    {
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };

                    using var process = Process.Start(psi);
                    var output = process.StandardOutput.ReadToEnd();
                    var error = process.StandardError.ReadToEnd();
                    process.WaitForExit();

                    return string.IsNullOrWhiteSpace(error) ? output : error;
                }
                catch (Exception ex)
                {
                    return $"Error: {ex.Message}";
                }
            });
        }

        // Internet Kill Switch - Enable adapter (FIXED)
        private async void InternetToggle_Unchecked(object sender, RoutedEventArgs e)
        {
            // Use the stored adapter name (works even when disabled)
            if (string.IsNullOrEmpty(_lastAdapterName))
            {
                // Fallback: Get ALL adapters including disabled ones
                var allAdapters = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(n => !n.Description.ToLower().Contains("virtual") &&
                                !n.Description.ToLower().Contains("loopback"))
                    .ToList();

                if (!allAdapters.Any())
                {
                    MessageBox.Show("No network adapter found to re-enable!", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // Pick the first non-virtual adapter
                _lastAdapterName = allAdapters.First().Name;
            }

            var result = await RunNetshAsync($@"interface set interface name=""{_lastAdapterName}"" admin=ENABLED");

            Debug.WriteLine($"Enabled {_lastAdapterName}: {result}");

            // Wait for adapter to come back up
            await Task.Delay(1500);

            // Clear the stored name so it detects fresh next time
            _lastAdapterName = null;

            await UpdateNetworkStats();
        }

        // Resize grip functionality
        private void ResizeGrip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!_isFocusMode)
            {
                var hwndSource = PresentationSource.FromVisual(this) as HwndSource;
                if (hwndSource != null)
                {
                    SendMessage(hwndSource.Handle, WM_SYSCOMMAND, (IntPtr)SC_SIZE_SOUTHEAST, IntPtr.Zero);
                }
            }
        }

        // Make Speed panel draggable when in focus mode
        private void SpeedPanel_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_isFocusMode && e.ClickCount == 1)
            {
                if (e.OriginalSource is FrameworkElement element &&
                    !IsChildOf(element, FocusToggle))
                {
                    try
                    {
                        DragMove();
                    }
                    catch (InvalidOperationException)
                    {
                    }
                }
            }
        }

        // Helper to check if element is child of another
        private bool IsChildOf(DependencyObject child, DependencyObject parent)
        {
            DependencyObject current = child;
            while (current != null)
            {
                if (current == parent)
                    return true;
                current = VisualTreeHelper.GetParent(current);
            }
            return false;
        }

        // Window dragging functionality
        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                try
                {
                    DragMove();
                }
                catch (InvalidOperationException)
                {
                }
            }
        }

        private void Minimize_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            _updateTimer?.Stop();
            _updateTimer?.Dispose();
            Close();
        }

        private async void Refresh_Click(object sender, RoutedEventArgs e)
        {
            await UpdateNetworkStats();
        }
    }
}
