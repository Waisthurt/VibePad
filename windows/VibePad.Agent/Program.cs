using System.Collections.Concurrent;
using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;

namespace VibePad.Agent;

internal sealed record LocalNetworkAddress(IPAddress Address, string InterfaceName, NetworkInterfaceType InterfaceType, bool HasDefaultGateway);

internal static class Program
{
    private const int Port = 8765;
    private const int MouseUdpPort = 8767;
    private const string InstanceMutexName = "VibePad.Agent.SingleInstance";
    internal const string ShowWindowEventName = "VibePad.Agent.ShowWindow";

    [STAThread]
    public static void Main()
    {
        using var instanceMutex = new Mutex(false, InstanceMutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            try
            {
                using var showWindow = EventWaitHandle.OpenExisting(ShowWindowEventName);
                showWindow.Set();
            }
            catch (WaitHandleCannotBeOpenedException) { }
            return;
        }

        using var showWindowEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowWindowEventName);
        ApplicationConfiguration.Initialize();
        using var runtime = new AgentRuntime(Port, MouseUdpPort);
        Application.Run(new VibePadTrayContext(runtime, showWindowEvent));
    }

    internal static IEnumerable<LocalNetworkAddress> LocalIpv4Addresses() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211)
            .SelectMany(n =>
            {
                var properties = n.GetIPProperties();
                var hasDefaultGateway = properties.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.Any.Equals(g.Address));
                return properties.UnicastAddresses
                    .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && IsPrivateLanAddress(a.Address))
                    .Select(a => new LocalNetworkAddress(a.Address, n.Name, n.NetworkInterfaceType, hasDefaultGateway));
            })
            .OrderByDescending(a => a.HasDefaultGateway)
            .ThenBy(a => a.InterfaceType == NetworkInterfaceType.Ethernet ? 0 : 1)
            .ThenBy(a => a.InterfaceName);

    private static bool IsPrivateLanAddress(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10
            || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
            || (bytes[0] == 192 && bytes[1] == 168);
    }
}

internal sealed class AgentServer(int port, ClipboardWorker clipboard, InputInjector input, MouseMotionBuffer mouseMotion, UdpMouseReceiver udpMouse, Action<string> reportStatus)
{
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.ListenAnyIP(port, listen => listen.Protocols = HttpProtocols.Http1));
        var app = builder.Build();
        app.UseWebSockets();
        app.Map("/vibepad/", HandleContextAsync);
        reportStatus($"正在等待手机连接（端口 {port}）");
        await app.StartAsync(cancellationToken);
        await app.WaitForShutdownAsync(cancellationToken);
    }

    private async Task HandleContextAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = 400;
            return;
        }

        WebSocket? socket = null;
        try
        {
            socket = await context.WebSockets.AcceptWebSocketAsync();
            udpMouse.AllowFrom(context.Connection.RemoteIpAddress);
            reportStatus($"已连接：{context.Connection.RemoteIpAddress}");
            await SendAsync(socket, new { type = "udp_ready", port = udpMouse.Port }, context.RequestAborted);
            await ReceiveLoopAsync(socket, context.RequestAborted, context.Connection.RemoteIpAddress);
        }
        catch (Exception e) when (e is not WebSocketException && e is not OperationCanceledException)
        {
            reportStatus($"连接错误：{e.Message}");
        }
        finally
        {
            mouseMotion.Clear();
            udpMouse.ClearAllowedAddress();
            input.ReleaseAll();
            if (socket is not null) socket.Dispose();
            reportStatus("等待手机连接");
        }
    }

    private async Task ReceiveLoopAsync(WebSocket socket, CancellationToken token, IPAddress? remoteAddress)
    {
        var buffer = new byte[64 * 1024];
        while (socket.State == WebSocketState.Open && !token.IsCancellationRequested)
        {
            using var content = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, token);
                if (result.MessageType == WebSocketMessageType.Close) return;
                content.Write(buffer, 0, result.Count);
                if (content.Length > 1_000_000) throw new InvalidOperationException("Message is too large.");
            } while (!result.EndOfMessage);

            if (result.MessageType != WebSocketMessageType.Text) continue;
            await DispatchAsync(socket, Encoding.UTF8.GetString(content.ToArray()), token, remoteAddress);
        }
    }

    private async Task DispatchAsync(WebSocket socket, string json, CancellationToken token, IPAddress? remoteAddress)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var type = root.GetProperty("type").GetString();
            switch (type)
            {
                case "ping":
                    await SendAsync(socket, new { type = "pong", timestamp = root.GetProperty("timestamp").GetInt64() }, token);
                    break;
                case "key":
                    HandleKey(root);
                    break;
                case "paste_text":
                    var id = root.GetProperty("requestId").GetString() ?? "";
                    var text = root.GetProperty("text").GetString() ?? "";
                    await clipboard.PasteAsync(text, input, token);
                    await SendAsync(socket, new { type = "paste_result", requestId = id, success = true, message = "pasted" }, token);
                    break;
                case "mouse_move":
                    if (!udpMouse.IsActiveFor(remoteAddress))
                        mouseMotion.Add(ClampMovement(root.GetProperty("dx").GetDouble()), ClampMovement(root.GetProperty("dy").GetDouble()));
                    break;
                case "clipboard":
                    HandleClipboard(root);
                    break;
                case "mouse_scroll":
                    input.Scroll(Math.Clamp(root.GetProperty("delta").GetInt32(), -1200, 1200));
                    break;
                case "mouse_button":
                    HandleMouseButton(root);
                    break;
                default:
                    await SendAsync(socket, new { type = "error", message = "Unsupported command." }, token);
                    break;
            }
        }
        catch (Exception e)
        {
            await SendAsync(socket, new { type = "error", message = e.Message }, token);
        }
    }

    private void HandleKey(JsonElement message)
    {
        var key = message.GetProperty("key").GetString();
        var action = message.GetProperty("action").GetString();
        if (key == "SCREENSHOT")
        {
            if (action != "press") throw new InvalidOperationException("Screenshot only supports press.");
            input.OpenScreenSnip();
            return;
        }
        var virtualKey = key switch
        {
            "ENTER" => 0x0D,
            "BACKSPACE" => 0x08,
            _ => throw new InvalidOperationException("Unsupported key.")
        };
        switch (action)
        {
            case "down": input.KeyDown(virtualKey); break;
            case "up": input.KeyUp(virtualKey); break;
            case "press": input.KeyDown(virtualKey); input.KeyUp(virtualKey); break;
            default: throw new InvalidOperationException("Unsupported key action.");
        }
    }

    private void HandleMouseButton(JsonElement message)
    {
        var button = message.GetProperty("button").GetString();
        var action = message.GetProperty("action").GetString();
        if (button is not ("left" or "right")) throw new InvalidOperationException("Unsupported mouse button.");
        if (action is not ("down" or "up" or "press")) throw new InvalidOperationException("Unsupported mouse action.");
        input.MouseButton(button, action);
    }

    private void HandleClipboard(JsonElement message)
    {
        switch (message.GetProperty("action").GetString())
        {
            case "copy": input.TapCtrlC(); break;
            case "paste": input.TapCtrlV(); break;
            default: throw new InvalidOperationException("Unsupported clipboard action.");
        }
    }

    private static double ClampMovement(double value)
    {
        if (!double.IsFinite(value)) throw new InvalidOperationException("Invalid mouse movement.");
        return Math.Clamp(value, -500d, 500d);
    }

    private static Task SendAsync(WebSocket socket, object payload, CancellationToken token) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload)), WebSocketMessageType.Text, true, token);
}

internal sealed class ClipboardWorker : IDisposable
{
    private readonly BlockingCollection<Func<Task>> _jobs = new();
    private readonly Thread _thread;

    public ClipboardWorker()
    {
        _thread = new Thread(() => { foreach (var job in _jobs.GetConsumingEnumerable()) job().GetAwaiter().GetResult(); }) { IsBackground = true, Name = "VibePad Clipboard" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public Task PasteAsync(string text, InputInjector input, CancellationToken token)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _jobs.Add(async () =>
        {
            try
            {
                Clipboard.SetText(text);
                await Task.Delay(60, token);
                input.TapCtrlV();
                completion.SetResult();
            }
            catch (Exception e) { completion.SetException(e); }
        }, token);
        return completion.Task;
    }

    public void Dispose() { _jobs.CompleteAdding(); _thread.Join(TimeSpan.FromSeconds(2)); _jobs.Dispose(); }
}

internal sealed class InputInjector
{
    private readonly HashSet<int> _heldKeys = [];
    private readonly HashSet<string> _heldMouseButtons = [];
    private readonly object _gate = new();

    public void KeyDown(int key) { lock (_gate) if (_heldKeys.Add(key)) Send(key, false); }
    public void KeyUp(int key) { lock (_gate) if (_heldKeys.Remove(key)) Send(key, true); }
    public void TapCtrlC() { KeyDown(0x11); KeyDown(0x43); KeyUp(0x43); KeyUp(0x11); }
    public void TapCtrlV() { KeyDown(0x11); KeyDown(0x56); KeyUp(0x56); KeyUp(0x11); }
    public void OpenScreenSnip()
    {
        lock (_gate)
        {
            // This is the Windows-wide screen-snipping shortcut (Win+Shift+S), rather
            // than the laptop-specific F10 function-key mapping.
            Send(0x5B, false, extended: true);
            Send(0x10, false);
            Send(0x53, false);
            Send(0x53, true);
            Send(0x10, true);
            Send(0x5B, true, extended: true);
        }
    }
    public void MoveMouse(int dx, int dy)
    {
        if (dx == 0 && dy == 0) return;
        lock (_gate) SendMouse(dx, dy, 0x0001, 0);
    }

    public void Scroll(int delta)
    {
        if (delta != 0) SendMouse(0, 0, 0x0800, unchecked((uint)delta));
    }

    public void MouseButton(string button, string action)
    {
        lock (_gate)
        {
            if (action == "down") MouseDown(button);
            else if (action == "up") MouseUp(button);
            else { MouseDown(button); MouseUp(button); }
        }
    }

    public void ReleaseAll()
    {
        lock (_gate)
        {
            foreach (var key in _heldKeys.ToArray()) { Send(key, true); _heldKeys.Remove(key); }
            foreach (var button in _heldMouseButtons.ToArray()) MouseUp(button);
        }
    }

    private void MouseDown(string button)
    {
        if (_heldMouseButtons.Add(button)) SendMouse(0, 0, button == "left" ? 0x0002u : 0x0008u, 0);
    }

    private void MouseUp(string button)
    {
        if (_heldMouseButtons.Remove(button)) SendMouse(0, 0, button == "left" ? 0x0004u : 0x0010u, 0);
    }

    private static void Send(int virtualKey, bool keyUp, bool extended = false)
    {
        var flags = (keyUp ? 0x0002u : 0u) | (extended ? 0x0001u : 0u);
        var input = new INPUT { type = 1, U = new InputUnion { ki = new KEYBDINPUT { wVk = (ushort)virtualKey, dwFlags = flags } } };
        if (SendInput(1, [input], Marshal.SizeOf<INPUT>()) != 1) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }

    private static void SendMouse(int dx, int dy, uint flags, uint mouseData)
    {
        var input = new INPUT { type = 0, U = new InputUnion { mi = new MOUSEINPUT { dx = dx, dy = dy, dwFlags = flags, mouseData = mouseData } } };
        if (SendInput(1, [input], Marshal.SizeOf<INPUT>()) != 1) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
    [StructLayout(LayoutKind.Sequential)] private struct INPUT { public uint type; public InputUnion U; }
    // INPUT's native union must include the largest member (MOUSEINPUT). Without it,
    // Marshal.SizeOf<INPUT>() is too small on 64-bit Windows and SendInput rejects it.
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }
    [StructLayout(LayoutKind.Sequential)] private struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public nint dwExtraInfo;
    }
    [StructLayout(LayoutKind.Sequential)] private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public nint dwExtraInfo; }
}

internal sealed class UdpMouseReceiver : IDisposable
{
    private readonly UdpClient _socket;
    private readonly MouseMotionBuffer _mouseMotion;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly object _addressGate = new();
    private readonly Task _receiveTask;
    private IPAddress? _allowedAddress;
    private bool _active;

    public int Port { get; }

    public UdpMouseReceiver(int port, MouseMotionBuffer mouseMotion)
    {
        Port = port;
        _mouseMotion = mouseMotion;
        _socket = new UdpClient(new IPEndPoint(IPAddress.Any, port));
        _receiveTask = Task.Run(ReceiveLoopAsync);
    }

    public void AllowFrom(IPAddress? address)
    {
        lock (_addressGate)
        {
            _allowedAddress = Normalize(address);
            _active = false;
        }
    }

    public bool IsActiveFor(IPAddress? address)
    {
        lock (_addressGate) return _active && _allowedAddress?.Equals(Normalize(address)) == true;
    }

    public void ClearAllowedAddress()
    {
        lock (_addressGate)
        {
            _allowedAddress = null;
            _active = false;
        }
    }

    private async Task ReceiveLoopAsync()
    {
        try
        {
            while (!_cancellation.IsCancellationRequested)
            {
                var datagram = await _socket.ReceiveAsync(_cancellation.Token);
                lock (_addressGate)
                {
                    if (_allowedAddress is null || !_allowedAddress.Equals(Normalize(datagram.RemoteEndPoint.Address))) continue;
                    _active = true;
                }
                if (datagram.Buffer.Length != 8) continue;
                var dx = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(datagram.Buffer.AsSpan(0, 4)));
                var dy = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(datagram.Buffer.AsSpan(4, 4)));
                if (float.IsFinite(dx) && float.IsFinite(dy)) _mouseMotion.Add(Math.Clamp(dx, -500f, 500f), Math.Clamp(dy, -500f, 500f));
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    private static IPAddress? Normalize(IPAddress? address) => address?.IsIPv4MappedToIPv6 == true ? address.MapToIPv4() : address;

    public void Dispose()
    {
        _cancellation.Cancel();
        _socket.Dispose();
        try { _receiveTask.Wait(TimeSpan.FromSeconds(1)); } catch (AggregateException) { }
        _cancellation.Dispose();
    }
}

internal sealed class MouseMotionBuffer : IDisposable
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Task _flushTask;
    private double _pendingX;
    private double _pendingY;

    public MouseMotionBuffer(InputInjector input)
    {
        _flushTask = Task.Run(() => FlushLoopAsync(input));
    }

    public void Add(double dx, double dy)
    {
        lock (_gate)
        {
            _pendingX += dx;
            _pendingY += dy;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _pendingX = 0;
            _pendingY = 0;
        }
    }

    private async Task FlushLoopAsync(InputInjector injector)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(8));
        try
        {
            while (await timer.WaitForNextTickAsync(_cancellation.Token))
            {
                int dx;
                int dy;
                lock (_gate)
                {
                    dx = (int)Math.Truncate(_pendingX);
                    dy = (int)Math.Truncate(_pendingY);
                    _pendingX -= dx;
                    _pendingY -= dy;
                }
                injector.MoveMouse(dx, dy);
            }
        }
        catch (OperationCanceledException) { }
    }

    public void Dispose()
    {
        _cancellation.Cancel();
        try { _flushTask.Wait(TimeSpan.FromSeconds(1)); } catch (AggregateException) { }
        _cancellation.Dispose();
    }
}
