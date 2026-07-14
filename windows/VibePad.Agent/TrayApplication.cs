using Microsoft.Win32;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace VibePad.Agent;

internal sealed class AgentRuntime : IDisposable
{
    private readonly CancellationTokenSource _cancellation = new();
    private readonly ClipboardWorker _clipboard = new();
    private readonly InputInjector _input = new();
    private readonly MouseMotionBuffer _mouseMotion;
    private readonly UdpMouseReceiver _udpMouse;
    private readonly AgentServer _server;
    private Task? _runTask;

    public event Action<string>? StatusChanged;
    public IReadOnlyList<LocalNetworkAddress> Addresses { get; }

    public AgentRuntime(int port, int mouseUdpPort)
    {
        Addresses = Program.LocalIpv4Addresses().ToArray();
        _mouseMotion = new MouseMotionBuffer(_input);
        _udpMouse = new UdpMouseReceiver(mouseUdpPort, _mouseMotion);
        _server = new AgentServer(port, _clipboard, _input, _mouseMotion, _udpMouse, ReportStatus);
    }

    public void Start()
    {
        if (_runTask is not null) return;
        ReportStatus("正在启动 VibePad Agent…");
        _runTask = Task.Run(async () =>
        {
            try { await _server.RunAsync(_cancellation.Token); }
            catch (OperationCanceledException) { }
            catch (Exception e) { ReportStatus($"服务启动失败：{e.Message}"); }
        });
    }

    private void ReportStatus(string status) => StatusChanged?.Invoke(status);

    public void Dispose()
    {
        _cancellation.Cancel();
        try { _runTask?.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        _input.ReleaseAll();
        _udpMouse.Dispose();
        _mouseMotion.Dispose();
        _clipboard.Dispose();
        _cancellation.Dispose();
    }
}

internal sealed class VibePadTrayContext : ApplicationContext
{
    private readonly AgentRuntime _runtime;
    private readonly EventWaitHandle _showWindowEvent;
    private readonly CancellationTokenSource _showWindowCancellation = new();
    private readonly Task _showWindowTask;
    private readonly NotifyIcon _trayIcon;
    private readonly StatusForm _window;
    private readonly ToolStripMenuItem _statusItem;

    public VibePadTrayContext(AgentRuntime runtime, EventWaitHandle showWindowEvent)
    {
        _runtime = runtime;
        _showWindowEvent = showWindowEvent;
        _window = new StatusForm(runtime.Addresses, ToggleStartup, ExitApplication);
        _window.FormClosing += HideOnClose;
        _statusItem = new ToolStripMenuItem("正在启动…") { Enabled = false };
        var startupItem = new ToolStripMenuItem("开机自动启动") { Checked = StartupRegistration.IsEnabled() };
        startupItem.Click += (_, _) =>
        {
            StartupRegistration.SetEnabled(!startupItem.Checked);
            startupItem.Checked = StartupRegistration.IsEnabled();
        };
        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("打开 VibePad", null, (_, _) => ShowWindow());
        menu.Items.Add(startupItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => ExitApplication());
        _trayIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "VibePad Agent",
            Visible = true,
            ContextMenuStrip = menu
        };
        _trayIcon.DoubleClick += (_, _) => ShowWindow();
        _runtime.StatusChanged += UpdateStatus;
        _showWindowTask = Task.Run(WaitForShowWindowRequests);
        _runtime.Start();
        ShowWindow();
    }

    private void WaitForShowWindowRequests()
    {
        while (!_showWindowCancellation.IsCancellationRequested)
        {
            if (!_showWindowEvent.WaitOne(250)) continue;
            if (_window.IsDisposed) return;
            try { _window.BeginInvoke(new Action(ShowWindow)); }
            catch (InvalidOperationException) { return; }
        }
    }

    private void UpdateStatus(string status)
    {
        if (_window.IsDisposed) return;
        if (_window.InvokeRequired)
        {
            _window.BeginInvoke(new Action(() => UpdateStatus(status)));
            return;
        }
        _statusItem.Text = status;
        _trayIcon.Text = $"VibePad：{status}".Length > 63 ? "VibePad Agent" : $"VibePad：{status}";
        _window.SetStatus(status);
    }

    private void ShowWindow()
    {
        _window.Show();
        _window.WindowState = FormWindowState.Normal;
        _window.Activate();
    }

    private void HideOnClose(object? sender, FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            _window.Hide();
        }
    }

    private void ToggleStartup(bool enabled) => StartupRegistration.SetEnabled(enabled);

    private void ExitApplication()
    {
        _trayIcon.Visible = false;
        _window.FormClosing -= HideOnClose;
        _window.Close();
        ExitThread();
    }

    protected override void ExitThreadCore()
    {
        _showWindowCancellation.Cancel();
        try { _showWindowTask.Wait(TimeSpan.FromSeconds(1)); } catch (AggregateException) { }
        _showWindowCancellation.Dispose();
        _runtime.Dispose();
        _trayIcon.Dispose();
        base.ExitThreadCore();
    }
}

internal sealed class StatusForm : Form
{
    private static readonly Color Background = Color.FromArgb(18, 16, 21);
    private static readonly Color Surface = Color.FromArgb(41, 37, 47);
    private static readonly Color Primary = Color.FromArgb(185, 154, 255);
    private static readonly Color OnSurface = Color.FromArgb(234, 228, 238);
    private static readonly Color Muted = Color.FromArgb(202, 194, 208);

    private readonly FlowLayoutPanel _connectionCard = new() { FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(16, 12, 16, 12), Dock = DockStyle.Fill, AutoSize = true };
    private readonly Label _status = new() { AutoSize = true, ForeColor = Primary, Font = new Font("Segoe UI", 11.5f, FontStyle.Bold) };
    private readonly Label _statusDetail = new() { AutoSize = true, ForeColor = Muted, Font = new Font("Segoe UI", 9) };

    private readonly FlowLayoutPanel _ipCard = new() { FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(16, 12, 16, 12), Dock = DockStyle.Fill, AutoSize = true };
    private readonly Label _recommendationLabel = new() { AutoSize = true, ForeColor = Color.FromArgb(150, 231, 177), Font = new Font("Segoe UI", 9, FontStyle.Bold), Text = "推荐填写这个地址" };
    private readonly Label _ipLabel = new() { AutoSize = true, ForeColor = Primary, Font = new Font("Segoe UI", 21, FontStyle.Bold) };
    private readonly System.Windows.Forms.Timer _copyTimer = new() { Interval = 1500 };

    public StatusForm(IReadOnlyList<LocalNetworkAddress> addresses, Action<bool> setStartup, Action exit)
    {
        Text = "VibePad Agent";
        Icon = SystemIcons.Application;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = true;
        ClientSize = new Size(400, 320);
        BackColor = Background;
        ForeColor = OnSurface;

        // Enable Windows Forms DPI scaling awareness
        this.AutoScaleDimensions = new System.Drawing.SizeF(7F, 15F);
        this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;

        var title = new Label { Text = "VibePad", AutoSize = true, ForeColor = Primary, Font = new Font("Segoe UI", 22, FontStyle.Bold), Margin = new Padding(0) };
        var subtitle = new Label { Text = "让手机成为你的 Vibe Coding 工具", AutoSize = true, ForeColor = Muted, Font = new Font("Segoe UI", 9.5f), Margin = new Padding(2, 2, 0, 0) };

        // 1. Connection Card
        _connectionCard.BackColor = Surface;
        _connectionCard.Controls.Add(_status);
        _connectionCard.Controls.Add(_statusDetail);

        _status.Margin = new Padding(0, 0, 0, 4);
        _statusDetail.Margin = new Padding(0);
        _statusDetail.Anchor = AnchorStyles.Left | AnchorStyles.Right;

        // 2. IP Card
        _ipCard.BackColor = Surface;
        _ipCard.Controls.Add(_recommendationLabel);
        _ipCard.Controls.Add(_ipLabel);

        _recommendationLabel.Margin = new Padding(0, 0, 0, 3);
        _ipLabel.Margin = new Padding(0, 0, 0, 6);

        if (addresses.Count == 0)
        {
            _ipLabel.Text = "无网络";
            _ipLabel.ForeColor = Color.FromArgb(255, 183, 167);
            var noNetworkLabel = new Label
            {
                Text = "未检测到可用 IP",
                AutoSize = true,
                ForeColor = Muted,
                Font = new Font("Segoe UI", 9),
                Margin = new Padding(0)
            };
            _ipCard.Controls.Add(noNetworkLabel);
        }
        else
        {
            _ipLabel.Text = addresses[0].Address.ToString();

            if (addresses.Count > 1)
            {
                var ipSelector = new ComboBox
                {
                    Height = 24,
                    DropDownStyle = ComboBoxStyle.DropDownList,
                    FlatStyle = FlatStyle.Flat,
                    BackColor = Surface,
                    ForeColor = OnSurface,
                    Font = new Font("Segoe UI", 9),
                    Margin = new Padding(0),
                    Anchor = AnchorStyles.Left | AnchorStyles.Right
                };

                foreach (var addr in addresses)
                {
                    ipSelector.Items.Add($"{FriendlyNetworkName(addr.InterfaceType)}: {addr.Address}");
                }
                ipSelector.SelectedIndex = 0;
                ipSelector.SelectedIndexChanged += (s, e) =>
                {
                    if (ipSelector.SelectedIndex >= 0 && ipSelector.SelectedIndex < addresses.Count)
                    {
                        _ipLabel.Text = addresses[ipSelector.SelectedIndex].Address.ToString();
                    }
                };
                _ipCard.Controls.Add(ipSelector);
            }
            else
            {
                var networkLabel = new Label
                {
                    Text = $"当前网络：{FriendlyNetworkName(addresses[0].InterfaceType)}",
                    AutoSize = true,
                    ForeColor = Muted,
                    Font = new Font("Segoe UI", 9),
                    Margin = new Padding(0)
                };
                _ipCard.Controls.Add(networkLabel);
            }
        }

        // 3. IP Click-to-copy & Hover events
        var copyHandler = new EventHandler((s, e) =>
        {
            if (addresses.Count > 0)
            {
                try
                {
                    Clipboard.SetText(_ipLabel.Text);
                    _recommendationLabel.Text = "✓ 已复制到剪贴板！";
                    _recommendationLabel.ForeColor = Color.FromArgb(130, 210, 160);
                    _copyTimer.Stop();
                    _copyTimer.Start();
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"复制失败：{ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        });

        _copyTimer.Tick += (s, e) =>
        {
            _recommendationLabel.Text = "推荐填写这个地址";
            _recommendationLabel.ForeColor = Color.FromArgb(150, 231, 177);
            _copyTimer.Stop();
        };

        var cardHoverBg = Color.FromArgb(50, 45, 57);
        WireHoverEvent(_ipCard, _ipCard, Surface, cardHoverBg);
        WireClickEvent(_ipCard, copyHandler);

        // 4. Bottom Panel (using TableLayoutPanel with AutoSize)
        var bottomTable = new TableLayoutPanel
        {
            Margin = new Padding(0),
            Dock = DockStyle.Fill,
            RowCount = 1,
            ColumnCount = 2,
            AutoSize = true
        };
        bottomTable.ColumnStyles.Clear();
        bottomTable.RowStyles.Clear();
        bottomTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 65F));
        bottomTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35F));

        var startup = new CheckBox
        {
            Text = "开机自动启动",
            AutoSize = true,
            Checked = StartupRegistration.IsEnabled(),
            ForeColor = OnSurface,
            Font = new Font("Segoe UI", 9.5f),
            Margin = new Padding(0, 0, 0, 4),
            Cursor = Cursors.Hand
        };
        startup.CheckedChanged += (_, _) => setStartup(startup.Checked);

        var hint = new Label
        {
            Text = "关闭窗口后，VibePad 会继续在系统托盘运行。",
            AutoSize = true,
            ForeColor = Muted,
            Font = new Font("Segoe UI", 8.5f),
            Margin = new Padding(2, 0, 0, 0)
        };

        var leftFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Padding = new Padding(0),
            Margin = new Padding(0),
            AutoSize = true
        };
        leftFlow.Controls.Add(startup);
        leftFlow.Controls.Add(hint);

        var quit = new Button
        {
            Text = "退出程序",
            FlatStyle = FlatStyle.Flat,
            BackColor = Surface,
            ForeColor = OnSurface,
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
            Cursor = Cursors.Hand,
            Anchor = AnchorStyles.Right | AnchorStyles.Top,
            Margin = new Padding(0, 2, 0, 0),
            AutoSize = true,
            Padding = new Padding(12, 6, 12, 6)
        };
        quit.FlatAppearance.BorderSize = 1;
        quit.FlatAppearance.BorderColor = Primary;
        quit.FlatAppearance.MouseOverBackColor = Color.FromArgb(61, 55, 70);
        quit.FlatAppearance.MouseDownBackColor = Color.FromArgb(81, 73, 93);
        quit.Click += (_, _) => exit();

        bottomTable.Controls.Add(leftFlow, 0, 0);
        bottomTable.Controls.Add(quit, 1, 0);

        // 5. Main Grid TableLayout
        var mainTable = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(24, 20, 24, 20),
            RowCount = 5,
            ColumnCount = 1
        };
        mainTable.ColumnStyles.Clear();
        mainTable.RowStyles.Clear();
        mainTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));

        mainTable.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // Row 0: Title Flow
        mainTable.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // Row 1: Connection Card
        mainTable.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // Row 2: IP Card
        mainTable.RowStyles.Add(new RowStyle(SizeType.Percent, 100F)); // Row 3: Spacer
        mainTable.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // Row 4: Bottom Table

        var titleFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Padding = new Padding(0),
            Margin = new Padding(0, 0, 0, 14),
            AutoSize = true
        };
        titleFlow.Controls.Add(title);
        titleFlow.Controls.Add(subtitle);

        mainTable.Controls.Add(titleFlow, 0, 0);
        mainTable.Controls.Add(_connectionCard, 0, 1);
        mainTable.Controls.Add(_ipCard, 0, 2);
        mainTable.Controls.Add(bottomTable, 0, 4);

        // Set card margins within mainTable rows
        _connectionCard.Margin = new Padding(0, 0, 0, 10);
        _ipCard.Margin = new Padding(0, 0, 0, 14);

        Controls.Add(mainTable);

        // Ensure text wraps dynamically to card width
        _connectionCard.SizeChanged += (s, e) =>
        {
            var innerWidth = _connectionCard.Width - _connectionCard.Padding.Left - _connectionCard.Padding.Right;
            if (innerWidth > 0)
            {
                _statusDetail.MaximumSize = new Size(innerWidth, 0);
            }
        };

        SetStatus("正在启动 VibePad Agent…");
    }

    private void WireClickEvent(Control control, EventHandler handler)
    {
        control.Click += handler;
        foreach (Control child in control.Controls)
        {
            if (child is not ComboBox)
            {
                WireClickEvent(child, handler);
            }
        }
    }

    private void WireHoverEvent(Control card, Control control, Color normalColor, Color hoverColor)
    {
        control.MouseEnter += (s, e) => { card.BackColor = hoverColor; };
        control.MouseLeave += (s, e) =>
        {
            var clientPos = card.PointToClient(Cursor.Position);
            if (!card.ClientRectangle.Contains(clientPos))
            {
                card.BackColor = normalColor;
            }
        };
        foreach (Control child in control.Controls)
        {
            if (child is not ComboBox)
            {
                WireHoverEvent(card, child, normalColor, hoverColor);
            }
        }
    }

    private static string FriendlyNetworkName(NetworkInterfaceType type) => type switch
    {
        NetworkInterfaceType.Wireless80211 => "Wi-Fi",
        NetworkInterfaceType.Ethernet => "以太网",
        _ => "网络"
    };

    public void SetStatus(string status)
    {
        if (status.StartsWith("已连接：", StringComparison.Ordinal))
        {
            var phoneIp = status["已连接：".Length..];
            _connectionCard.BackColor = Color.FromArgb(24, 48, 38);
            _status.ForeColor = Color.FromArgb(130, 210, 160);
            _status.Text = "● 手机已成功连接";
            _statusDetail.Text = $"已连接手机：\n{phoneIp}\n可以开始在手机端输入。";
            return;
        }

        if (status.Contains("失败", StringComparison.Ordinal) || status.Contains("错误", StringComparison.Ordinal))
        {
            _connectionCard.BackColor = Color.FromArgb(58, 30, 36);
            _status.ForeColor = Color.FromArgb(240, 150, 140);
            _status.Text = "● 连接遇到问题";
            _statusDetail.Text = status;
            return;
        }

        _connectionCard.BackColor = Surface;
        _status.ForeColor = Primary;
        _status.Text = "● 等待手机连接";
        _statusDetail.Text = "打开手机端 VibePad，填入下方 IP 地址并点击连接以开始输入。";
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int valueSize);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        var darkMode = 1;
        var captionColor = ColorTranslator.ToWin32(Color.FromArgb(35, 45, 103));
        var textColor = ColorTranslator.ToWin32(OnSurface);
        DwmSetWindowAttribute(Handle, 20, ref darkMode, sizeof(int));
        DwmSetWindowAttribute(Handle, 35, ref captionColor, sizeof(int));
        DwmSetWindowAttribute(Handle, 36, ref textColor, sizeof(int));
    }
}

internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "VibePad Agent";

    public static bool IsEnabled() => Registry.CurrentUser.OpenSubKey(RunKey, false)?.GetValue(ValueName) is string;

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, true);
        if (enabled)
        {
            var executable = Path.Combine(AppContext.BaseDirectory, "VibePad.Agent.exe");
            key.SetValue(ValueName, $"\"{executable}\"");
        }
        else key.DeleteValue(ValueName, false);
    }
}
