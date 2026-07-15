package com.vibepad

import android.content.Context
import android.os.Handler
import android.os.Looper
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.Response
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import org.json.JSONObject
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.util.UUID
import java.util.concurrent.Executors
import java.util.concurrent.ScheduledFuture
import java.util.concurrent.TimeUnit

enum class ConnectionState { DISCONNECTED, CONNECTING, CONNECTED, FAILED }

class VibeSocket(context: Context) {
    private val preferences = context.getSharedPreferences("vibepad", Context.MODE_PRIVATE)
    private val main = Handler(Looper.getMainLooper())
    private val client = OkHttpClient.Builder().pingInterval(20, TimeUnit.SECONDS).build()
    private val udpSocket = DatagramSocket()
    private val movementLock = Any()
    private val movementScheduler = Executors.newSingleThreadScheduledExecutor { runnable ->
        Thread(runnable, "VibePad mouse motion").apply { isDaemon = true }
    }
    private val reconnectScheduler = Executors.newSingleThreadScheduledExecutor { runnable ->
        Thread(runnable, "VibePad reconnect").apply { isDaemon = true }
    }
    private var socket: WebSocket? = null
    private var reconnectTask: ScheduledFuture<*>? = null
    @Volatile private var connected = false
    @Volatile private var udpReady = false
    @Volatile private var udpScrollReady = false
    @Volatile private var udpDestination: InetAddress? = null
    @Volatile private var appInForeground = true
    @Volatile private var userDisconnected = false
    @Volatile private var connectionGeneration = 0
    private var reconnectTarget: String? = preferences.getString("last_successful_host", null)
    private var reconnectAttempt = 0
    private var pendingDx = 0f
    private var pendingDy = 0f
    private var pendingScroll = 0f

    var state by mutableStateOf(ConnectionState.DISCONNECTED)
        private set
    var statusMessage by mutableStateOf("未连接")
        private set
    var savedHost: String = preferences.getString("host", "") ?: ""
        private set
    var mouseSensitivity by mutableFloatStateOf(preferences.getFloat("mouse_sensitivity", 1.35f))
        private set
    var scrollSensitivity by mutableFloatStateOf(preferences.getFloat("scroll_sensitivity", 1f))
        private set
    var autoReconnectEnabled by mutableStateOf(preferences.getBoolean("auto_reconnect", true))
        private set

    init {
        // Mouse events can arrive in bursts. Flush the combined relative movement at a
        // stable cadence so Wi-Fi packet timing does not become visible cursor jitter.
        movementScheduler.scheduleAtFixedRate(::flushRemoteInput, 8, 8, TimeUnit.MILLISECONDS)
    }

    fun connect(host: String) {
        val normalized = host.trim().removePrefix("ws://").removePrefix("http://").substringBefore('/')
        if (normalized.isBlank()) { showFailure("请输入电脑 IP 地址"); return }
        savedHost = normalized
        preferences.edit().putString("host", normalized).apply()
        reconnectTarget = normalized
        reconnectAttempt = 0
        userDisconnected = false
        cancelReconnect()
        startConnection(normalized, automatic = false)
    }

    fun onAppForeground() {
        appInForeground = true
        if (autoReconnectEnabled && !userDisconnected && !connected) {
            val target = reconnectTarget ?: return
            reconnectAttempt = 0
            cancelReconnect()
            startConnection(target, automatic = true)
        }
    }

    fun onAppBackground() {
        appInForeground = false
        cancelReconnect()
    }

    fun updateAutoReconnect(enabled: Boolean) {
        autoReconnectEnabled = enabled
        preferences.edit().putBoolean("auto_reconnect", enabled).apply()
        if (!enabled) cancelReconnect() else onAppForeground()
    }

    private fun startConnection(host: String, automatic: Boolean) {
        val generation = ++connectionGeneration
        socket?.cancel()
        clearPendingInput()
        connected = false
        udpReady = false
        udpScrollReady = false
        udpDestination = runCatching { InetAddress.getByName(host) }.getOrNull()
        update(ConnectionState.CONNECTING, if (automatic) "正在自动重连 $host" else "正在连接 $host")
        socket = client.newWebSocket(
            Request.Builder().url("ws://$host:8765/vibepad/").build(),
            object : WebSocketListener() {
                override fun onOpen(webSocket: WebSocket, response: Response) {
                    if (generation != connectionGeneration) return
                    connected = true
                    reconnectAttempt = 0
                    reconnectTarget = host
                    preferences.edit().putString("last_successful_host", host).apply()
                    update(ConnectionState.CONNECTED, "已连接：$host")
                }
                override fun onMessage(webSocket: WebSocket, text: String) {
                    if (generation == connectionGeneration) handleMessage(text)
                }
                override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) {
                    if (generation != connectionGeneration) return
                    connected = false
                    udpReady = false
                    udpScrollReady = false
                    scheduleReconnect(host, t.message ?: "请检查 IP 和防火墙")
                }
                override fun onClosed(webSocket: WebSocket, code: Int, reason: String) {
                    if (generation != connectionGeneration) return
                    connected = false
                    udpReady = false
                    udpScrollReady = false
                    scheduleReconnect(host, "连接已断开")
                }
            }
        )
    }

    fun disconnect() {
        userDisconnected = true
        reconnectTarget = null
        cancelReconnect()
        connectionGeneration++
        connected = false
        udpReady = false
        udpScrollReady = false
        socket?.close(1000, "user disconnected")
        socket = null
        if (state != ConnectionState.DISCONNECTED) update(ConnectionState.DISCONNECTED, "未连接")
    }

    fun paste(text: String) {
        if (text.isBlank()) return
        send(JSONObject().put("type", "paste_text").put("requestId", UUID.randomUUID().toString()).put("text", text))
    }

    fun key(key: String, action: String) = send(JSONObject().put("type", "key").put("key", key).put("action", action))

    fun moveMouse(dx: Float, dy: Float) {
        if (!connected) return
        synchronized(movementLock) {
            pendingDx += dx * mouseSensitivity
            pendingDy += dy * mouseSensitivity
        }
    }

    fun updateMouseSensitivity(value: Float) {
        mouseSensitivity = value.coerceIn(0.5f, 3f)
        preferences.edit().putFloat("mouse_sensitivity", mouseSensitivity).apply()
    }

    fun updateScrollSensitivity(value: Float) {
        scrollSensitivity = value.coerceIn(0.5f, 3f)
        preferences.edit().putFloat("scroll_sensitivity", scrollSensitivity).apply()
    }

    private fun scheduleReconnect(host: String, reason: String) {
        if (!autoReconnectEnabled || !appInForeground || userDisconnected) {
            update(ConnectionState.DISCONNECTED, reason)
            return
        }
        if (reconnectTask?.isDone == false) return
        val delays = longArrayOf(1_000, 2_000, 5_000, 10_000)
        if (reconnectAttempt >= delays.size) {
            update(ConnectionState.DISCONNECTED, "未连接，点击连接重试")
            return
        }
        val attempt = reconnectAttempt++
        update(ConnectionState.CONNECTING, "正在自动重连 $host（${attempt + 1}/${delays.size}）")
        cancelReconnect()
        reconnectTask = reconnectScheduler.schedule(
            {
                if (appInForeground && autoReconnectEnabled && !userDisconnected && !connected) {
                    startConnection(host, automatic = true)
                }
            },
            delays[attempt],
            TimeUnit.MILLISECONDS
        )
    }

    private fun cancelReconnect() {
        reconnectTask?.cancel(false)
        reconnectTask = null
    }

    fun mouseButton(button: String, action: String) =
        send(JSONObject().put("type", "mouse_button").put("button", button).put("action", action))

    fun scroll(delta: Int) {
        if (!connected || delta == 0) return
        synchronized(movementLock) {
            pendingScroll = (pendingScroll + delta * scrollSensitivity).coerceIn(-1200f, 1200f)
        }
    }

    fun clipboard(action: String) = send(JSONObject().put("type", "clipboard").put("action", action))

    fun dispose() {
        disconnect()
        movementScheduler.shutdownNow()
        reconnectScheduler.shutdownNow()
        udpSocket.close()
        client.dispatcher.executorService.shutdown()
        client.connectionPool.evictAll()
    }

    private fun send(payload: JSONObject) {
        if (state != ConnectionState.CONNECTED || socket?.send(payload.toString()) != true) showFailure("未连接到电脑")
    }

    private fun flushRemoteInput() {
        if (!connected) return
        val input = synchronized(movementLock) {
            val scroll = pendingScroll.toInt()
            val result = Triple(pendingDx, pendingDy, scroll)
            pendingDx = 0f
            pendingDy = 0f
            pendingScroll -= scroll
            result
        }
        if (input.first != 0f || input.second != 0f) {
            if (udpReady) {
                udpDestination?.let { destination ->
                    val packetBytes = ByteBuffer.allocate(8)
                        .order(ByteOrder.LITTLE_ENDIAN)
                        .putFloat(input.first)
                        .putFloat(input.second)
                        .array()
                    runCatching { udpSocket.send(DatagramPacket(packetBytes, packetBytes.size, destination, 8767)) }
                }
            }
            // This is a safety fallback. A new Windows Agent ignores it after receiving its
            // first UDP packet; an older or firewall-blocked Agent still receives movement.
            val payload = JSONObject()
                .put("type", "mouse_move")
                .put("dx", input.first)
                .put("dy", input.second)
            socket?.send(payload.toString())
        }
        if (input.third != 0) {
            if (udpScrollReady) {
                udpDestination?.let { destination ->
                    val packetBytes = ByteBuffer.allocate(5)
                        .order(ByteOrder.LITTLE_ENDIAN)
                        .put(1)
                        .putInt(input.third)
                        .array()
                    runCatching { udpSocket.send(DatagramPacket(packetBytes, packetBytes.size, destination, 8767)) }
                }
            }
            // Keep a WebSocket fallback until the Agent has received a UDP scroll packet.
            socket?.send(JSONObject().put("type", "mouse_scroll").put("delta", input.third).toString())
        }
    }

    private fun clearPendingInput() = synchronized(movementLock) {
        pendingDx = 0f
        pendingDy = 0f
        pendingScroll = 0f
    }

    private fun handleMessage(text: String) {
        val json = runCatching { JSONObject(text) }.getOrNull() ?: return
        when (json.optString("type")) {
            "paste_result" -> update(ConnectionState.CONNECTED, if (json.optBoolean("success")) "已粘贴到电脑" else "粘贴失败")
            "udp_ready" -> {
                udpReady = true
                udpScrollReady = json.optBoolean("scroll", false)
            }
            "error" -> update(ConnectionState.CONNECTED, "错误：${json.optString("message")}")
        }
    }

    private fun update(nextState: ConnectionState, text: String) = main.post { state = nextState; statusMessage = text }
    private fun showFailure(text: String) = update(ConnectionState.FAILED, text)
}
