using System.Collections.Concurrent;
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

internal static class Program
{
    private const int Port = 8765;
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
        using var runtime = new AgentRuntime(Port);
        Application.Run(new VibePadTrayContext(runtime, showWindowEvent));
    }

    internal static IEnumerable<IPAddress> LocalIpv4Addresses() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address)
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a));
}

internal sealed class AgentServer(int port, ClipboardWorker clipboard, InputInjector input, MouseMotionBuffer mouseMotion, Action<string> reportStatus)
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
            reportStatus($"已连接：{context.Connection.RemoteIpAddress}");
            await ReceiveLoopAsync(socket, context.RequestAborted);
        }
        catch (Exception e) when (e is not WebSocketException && e is not OperationCanceledException)
        {
            reportStatus($"连接错误：{e.Message}");
        }
        finally
        {
            mouseMotion.Clear();
            input.ReleaseAll();
            if (socket is not null) socket.Dispose();
            reportStatus("等待手机连接");
        }
    }

    private async Task ReceiveLoopAsync(WebSocket socket, CancellationToken token)
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
            await DispatchAsync(socket, Encoding.UTF8.GetString(content.ToArray()), token);
        }
    }

    private async Task DispatchAsync(WebSocket socket, string json, CancellationToken token)
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
                    mouseMotion.Add(ClampMovement(root.GetProperty("dx").GetDouble()), ClampMovement(root.GetProperty("dy").GetDouble()));
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
        var virtualKey = key switch { "ENTER" => 0x0D, "BACKSPACE" => 0x08, _ => throw new InvalidOperationException("Unsupported key.") };
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

    private static int ClampMovement(double value)
    {
        if (!double.IsFinite(value)) throw new InvalidOperationException("Invalid mouse movement.");
        return (int)Math.Clamp(Math.Round(value), -500d, 500d);
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
    public void TapCtrlV() { KeyDown(0x11); KeyDown(0x56); KeyUp(0x56); KeyUp(0x11); }
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

    private static void Send(int virtualKey, bool keyUp)
    {
        var input = new INPUT { type = 1, U = new InputUnion { ki = new KEYBDINPUT { wVk = (ushort)virtualKey, dwFlags = keyUp ? 0x0002u : 0u } } };
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

    public void Add(int dx, int dy)
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
