using Microsoft.Win32;
using System.Net;

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
    public IReadOnlyList<IPAddress> Addresses { get; }

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
    private readonly Label _status = new() { AutoSize = true, ForeColor = Color.FromArgb(92, 54, 166), Font = new Font("Segoe UI", 11, FontStyle.Bold) };

    public StatusForm(IReadOnlyList<IPAddress> addresses, Action<bool> setStartup, Action exit)
    {
        Text = "VibePad Agent";
        Icon = SystemIcons.Application;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = true;
        ClientSize = new Size(620, 420);
        MinimumSize = new Size(620, 420);

        var title = new Label { Text = "VibePad", AutoSize = true, Font = new Font("Segoe UI", 20, FontStyle.Bold) };
        var endpointText = addresses.Count == 0 ? "未检测到局域网 IPv4 地址" : string.Join(Environment.NewLine, addresses.Select(ip => $"ws://{ip}:8765/vibepad/"));
        var endpoints = new TextBox
        {
            Text = endpointText,
            ReadOnly = true,
            Multiline = true,
            BorderStyle = BorderStyle.None,
            BackColor = BackColor,
            ForeColor = Color.DimGray,
            Width = 560,
            Height = 110
        };
        var startup = new CheckBox { Text = "开机自动启动", AutoSize = true, Checked = StartupRegistration.IsEnabled() };
        startup.CheckedChanged += (_, _) => setStartup(startup.Checked);
        var hint = new Label { Text = "关闭此窗口会继续在系统托盘中运行。", AutoSize = true, ForeColor = Color.DimGray };
        var quit = new Button { Text = "退出程序", AutoSize = true };
        quit.Click += (_, _) => exit();

        var layout = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(32),
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false
        };
        layout.Controls.Add(title);
        layout.Controls.Add(_status);
        layout.Controls.Add(new Label { Text = "本机连接地址", AutoSize = true, Margin = new Padding(3, 16, 3, 0) });
        layout.Controls.Add(endpoints);
        layout.Controls.Add(startup);
        layout.Controls.Add(hint);
        layout.Controls.Add(quit);
        Controls.Add(layout);
    }

    public void SetStatus(string status) => _status.Text = status;
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
