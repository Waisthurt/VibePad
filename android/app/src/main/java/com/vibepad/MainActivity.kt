package com.vibepad

import android.os.Bundle
import android.view.HapticFeedbackConstants
import android.view.WindowManager
import androidx.activity.ComponentActivity
import androidx.activity.SystemBarStyle
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.gestures.awaitEachGesture
import androidx.compose.foundation.gestures.awaitFirstDown
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.interaction.collectIsPressedAsState
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Slider
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.rememberModalBottomSheetState
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.outlined.Settings
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.ExperimentalComposeUiApi
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.toArgb
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.platform.LocalUriHandler
import androidx.compose.ui.platform.LocalView
import androidx.compose.ui.text.input.KeyboardCapitalization
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.lifecycle.DefaultLifecycleObserver
import androidx.lifecycle.LifecycleOwner
import androidx.lifecycle.ProcessLifecycleOwner
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlin.math.abs
import kotlin.math.sqrt

private val VibePadColors = darkColorScheme(
    primary = Color(0xFFB99AFF),
    secondary = Color(0xFFD2BCFF),
    surface = Color(0xFF17151B),
    surfaceVariant = Color(0xFF29252F),
    background = Color(0xFF121015),
    onSurface = Color(0xFFEAE4EE),
    onSurfaceVariant = Color(0xFFCAC2D0)
)

class MainActivity : ComponentActivity() {
    private lateinit var connection: VibeSocket

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge(
            statusBarStyle = SystemBarStyle.dark(Color.Transparent.toArgb()),
            navigationBarStyle = SystemBarStyle.dark(Color.Transparent.toArgb())
        )
        // Keep the control surface at its normal size while the IME overlays its lower area.
        // Some Android 15 devices ignore the manifest value unless it is applied to the window.
        window.setSoftInputMode(WindowManager.LayoutParams.SOFT_INPUT_ADJUST_NOTHING)
        connection = VibeSocket(applicationContext)
        ProcessLifecycleOwner.get().lifecycle.addObserver(object : DefaultLifecycleObserver {
            override fun onStart(owner: LifecycleOwner) = connection.onAppForeground()
            override fun onStop(owner: LifecycleOwner) {
                releaseRemoteInput()
                connection.onAppBackground()
            }
        })
        setContent { MaterialTheme(colorScheme = VibePadColors) { VibePadScreen(connection) } }
    }

    override fun onDestroy() {
        releaseRemoteInput()
        connection.dispose()
        super.onDestroy()
    }

    private fun releaseRemoteInput() {
        connection.key("BACKSPACE", "up")
        connection.mouseButton("left", "up")
        connection.mouseButton("right", "up")
    }
}

@Composable
private fun VibePadScreen(connection: VibeSocket) {
    var host by remember { mutableStateOf(connection.savedHost) }
    var text by remember { mutableStateOf("") }
    var showSettings by remember { mutableStateOf(false) }
    val view = LocalView.current

    LaunchedEffect(connection.extractedSelection) {
        val extracted = connection.extractedSelection ?: return@LaunchedEffect
        text = if (text.isBlank()) extracted else "$text\n$extracted"
        connection.consumeExtractedSelection()
    }

    Column(
        modifier = Modifier
            .fillMaxSize()
            .background(MaterialTheme.colorScheme.background)
            // Deliberately exclude IME insets: the keyboard should overlay the lower
            // controls instead of shrinking the control surface while typing.
            .statusBarsPadding()
            .navigationBarsPadding()
            .padding(horizontal = 14.dp, vertical = 8.dp),
        verticalArrangement = Arrangement.spacedBy(8.dp)
    ) {
        Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.fillMaxWidth()) {
            Text("VibePad", style = MaterialTheme.typography.headlineSmall, color = MaterialTheme.colorScheme.primary)
            IconButton(onClick = { showSettings = true }) {
                Icon(Icons.Outlined.Settings, contentDescription = "控制设置", tint = MaterialTheme.colorScheme.onSurfaceVariant)
            }
            IconButton(onClick = { connection.key("SCREENSHOT", "press"); view.performHapticFeedback(HapticFeedbackConstants.KEYBOARD_TAP) }) {
                Text("截图", style = MaterialTheme.typography.labelLarge, color = MaterialTheme.colorScheme.primary)
            }
            Box(modifier = Modifier.weight(1f))
            Text(
                connection.statusMessage,
                style = MaterialTheme.typography.labelMedium,
                color = when (connection.state) {
                    ConnectionState.CONNECTED -> MaterialTheme.colorScheme.primary
                    ConnectionState.FAILED -> MaterialTheme.colorScheme.error
                    else -> MaterialTheme.colorScheme.onSurfaceVariant
                },
                maxLines = 1
            )
        }
        Row(horizontalArrangement = Arrangement.spacedBy(8.dp), modifier = Modifier.fillMaxWidth()) {
            OutlinedTextField(
                value = host,
                onValueChange = { host = it },
                label = { Text("电脑 IP") },
                singleLine = true,
                modifier = Modifier.weight(1f)
            )
            Button(onClick = { connection.connect(host) }, modifier = Modifier.height(56.dp)) { Text("连接") }
        }
        OutlinedTextField(
            value = text,
            onValueChange = { text = it },
            label = { Text("输入文字或使用系统语音输入") },
            keyboardOptions = KeyboardOptions(capitalization = KeyboardCapitalization.Sentences),
            // 六个快捷键改为横向胶囊后，把省下的空间优先还给输入框。
            modifier = Modifier.fillMaxWidth().height(142.dp),
            minLines = 3,
            maxLines = 5
        )
        Row(horizontalArrangement = Arrangement.spacedBy(8.dp), modifier = Modifier.fillMaxWidth()) {
            ActionButton("复制", Modifier.weight(1f)) { connection.clipboard("copy"); view.performHapticFeedback(HapticFeedbackConstants.KEYBOARD_TAP) }
            ActionButton("粘贴", Modifier.weight(1f)) { connection.clipboard("paste"); view.performHapticFeedback(HapticFeedbackConstants.KEYBOARD_TAP) }
            ActionButton(if (connection.selectionAvailable) "提取" else "全选", Modifier.weight(1f)) { connection.smartSelection(); view.performHapticFeedback(HapticFeedbackConstants.KEYBOARD_TAP) }
            ActionButton("上传", Modifier.weight(1f)) { connection.paste(text); if (connection.state == ConnectionState.CONNECTED) text = "" }
            ActionButton("回车", Modifier.weight(1f)) { connection.key("ENTER", "press"); view.performHapticFeedback(HapticFeedbackConstants.KEYBOARD_TAP) }
            HoldBackspace(connection, Modifier.weight(1f))
        }
        Touchpad(connection, Modifier.fillMaxWidth().weight(1f))
    }
    if (showSettings) {
        ControlSettings(connection, onDismiss = { showSettings = false })
    }
}

@Composable
@OptIn(ExperimentalMaterial3Api::class)
private fun ControlSettings(connection: VibeSocket, onDismiss: () -> Unit) {
    val sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true)
    val uriHandler = LocalUriHandler.current
    // 设置内容需要完整显示；跳过默认的半展开停靠点，打开即到最高位置。
    ModalBottomSheet(
        onDismissRequest = onDismiss,
        containerColor = MaterialTheme.colorScheme.surface,
        sheetState = sheetState
    ) {
        Column(
            modifier = Modifier
                .padding(horizontal = 24.dp, vertical = 8.dp)
                // 设置弹层同样需要避开三键/手势导航栏，不能让最后一项被系统栏盖住。
                .navigationBarsPadding()
                .padding(bottom = 28.dp)
        ) {
            Text("控制设置", style = MaterialTheme.typography.titleLarge)
            Text("鼠标灵敏度", style = MaterialTheme.typography.titleMedium, modifier = Modifier.padding(top = 24.dp))
            Text("${String.format("%.1f", connection.mouseSensitivity)}×", color = MaterialTheme.colorScheme.primary)
            Slider(
                value = connection.mouseSensitivity,
                onValueChange = connection::updateMouseSensitivity,
                valueRange = 0.5f..3f,
                steps = 24
            )
            Text("滚动灵敏度", style = MaterialTheme.typography.titleMedium, modifier = Modifier.padding(top = 24.dp))
            Text("${String.format("%.1f", connection.scrollSensitivity)}×", color = MaterialTheme.colorScheme.primary)
            Slider(
                value = connection.scrollSensitivity,
                onValueChange = connection::updateScrollSensitivity,
                valueRange = 0.5f..3f,
                steps = 24
            )
            Text("边缘移动速度", style = MaterialTheme.typography.titleMedium, modifier = Modifier.padding(top = 24.dp))
            Text("${String.format("%.1f", connection.edgeMotionSpeed)}×", color = MaterialTheme.colorScheme.primary)
            Slider(
                value = connection.edgeMotionSpeed,
                onValueChange = connection::updateEdgeMotionSpeed,
                valueRange = 0.5f..3f,
                steps = 24
            )
            Text(
                "长按拖拽时，手指停在触控板边缘会持续移动鼠标",
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant
            )
            Row(
                modifier = Modifier.fillMaxWidth().padding(top = 24.dp),
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.SpaceBetween
            ) {
                Column(modifier = Modifier.weight(1f)) {
                    Text("自动重连", style = MaterialTheme.typography.titleMedium)
                    Text("回到前台后自动连接上次成功的电脑", style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                }
                Switch(
                    checked = connection.autoReconnectEnabled,
                    onCheckedChange = connection::updateAutoReconnect
                )
            }
            Text("关于", style = MaterialTheme.typography.titleMedium, modifier = Modifier.padding(top = 24.dp))
            Text(
                "版本 0.1.5",
                style = MaterialTheme.typography.bodyMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                modifier = Modifier.padding(top = 6.dp)
            )
            Text(
                "最新版地址：github.com/Waisthurt/VibePad",
                style = MaterialTheme.typography.bodyMedium,
                color = MaterialTheme.colorScheme.primary,
                modifier = Modifier
                    .padding(top = 4.dp)
                    .clickable { uriHandler.openUri("https://github.com/Waisthurt/VibePad") }
            )
        }
    }
}

@Composable
private fun ActionButton(label: String, modifier: Modifier, onClick: () -> Unit) {
    Button(
        onClick = onClick,
        // 保持六等分宽度和文字居中；较矮的高度让按钮成为横向胶囊，
        // 同时为输入栏与触控板释放垂直空间。
        modifier = modifier.height(42.dp),
        colors = ButtonDefaults.buttonColors(),
        contentPadding = PaddingValues(horizontal = 4.dp)
    ) {
        Text(label, maxLines = 1, softWrap = false, fontSize = 16.sp, textAlign = TextAlign.Center)
    }
}

@Composable
@OptIn(ExperimentalComposeUiApi::class)
private fun Touchpad(connection: VibeSocket, modifier: Modifier) {
    val view = LocalView.current
    val edgeThreshold = with(LocalDensity.current) { 28.dp.toPx() }
    Box(
        modifier = modifier
            .background(MaterialTheme.colorScheme.surfaceVariant, MaterialTheme.shapes.medium)
            .border(1.dp, MaterialTheme.colorScheme.outlineVariant, MaterialTheme.shapes.medium)
            .pointerInput(connection, edgeThreshold) {
                coroutineScope {
                    awaitEachGesture {
                        val first = awaitFirstDown(requireUnconsumed = false)
                        var lastPosition = first.position
                        var lastTwoFingerY = 0f
                        var moved = false
                        var multiTouch = false
                        var twoFingerMoved = false
                        var dragging = false
                        var edgeDirection = Offset.Zero
                        var edgeMotionJob: kotlinx.coroutines.Job? = null

                        fun updateEdgeMotion(position: Offset) {
                            if (!dragging) return
                            val directionX = when {
                                position.x <= edgeThreshold -> -1f
                                position.x >= size.width - edgeThreshold -> 1f
                                else -> 0f
                            }
                            val directionY = when {
                                position.y <= edgeThreshold -> -1f
                                position.y >= size.height - edgeThreshold -> 1f
                                else -> 0f
                            }
                            edgeDirection = Offset(directionX, directionY)
                            if (edgeDirection == Offset.Zero) {
                                edgeMotionJob?.cancel()
                                edgeMotionJob = null
                            } else if (edgeMotionJob == null || edgeMotionJob?.isActive != true) {
                                edgeMotionJob = launch {
                                    // 以 125Hz 持续补充相对位移，和常规鼠标移动使用同一条
                                    // 平滑传输链路。对角线先归一化，确保总速度保持不变。
                                    val frameMillis = 8L
                                    val baseSpeed = 720f
                                    while (dragging && edgeDirection != Offset.Zero) {
                                        val length = sqrt(edgeDirection.x * edgeDirection.x + edgeDirection.y * edgeDirection.y)
                                        val distance = baseSpeed * connection.edgeMotionSpeed * frameMillis / 1000f
                                        connection.moveMouse(
                                            edgeDirection.x / length * distance,
                                            edgeDirection.y / length * distance
                                        )
                                        delay(frameMillis)
                                    }
                                }
                            }
                        }
                        val dragStarter = launch {
                            delay(400)
                            if (!multiTouch && !moved) {
                                dragging = true
                                connection.mouseButton("left", "down")
                                view.performHapticFeedback(HapticFeedbackConstants.LONG_PRESS)
                                updateEdgeMotion(lastPosition)
                            }
                        }
                        var anyPressed: Boolean
                        do {
                            val event = awaitPointerEvent()
                            val pressed = event.changes.filter { it.pressed }
                            if (pressed.size >= 2) {
                                multiTouch = true
                                if (dragging) {
                                    edgeMotionJob?.cancel()
                                    edgeMotionJob = null
                                    edgeDirection = Offset.Zero
                                    connection.mouseButton("left", "up")
                                    dragging = false
                                }
                                val averageY = pressed.take(2).map { it.position.y }.average().toFloat()
                                if (lastTwoFingerY != 0f) {
                                    val delta = ((averageY - lastTwoFingerY) * 4f).toInt()
                                    if (abs(delta) >= 1) {
                                        connection.scroll(delta)
                                        twoFingerMoved = twoFingerMoved || abs(delta) >= 8
                                    }
                                }
                                lastTwoFingerY = averageY
                            } else if (pressed.size == 1 && !multiTouch) {
                                // Compose may coalesce several hardware touch samples into one
                                // event. Replay its historical positions so the cursor follows the
                                // phone's actual sampling cadence instead of only UI frame cadence.
                                val change = pressed.first()
                                for (position in change.historical.map { it.position } + change.position) {
                                    val dx = position.x - lastPosition.x
                                    val dy = position.y - lastPosition.y
                                    if (abs(dx) >= 0.05f || abs(dy) >= 0.05f) {
                                        moved = true
                                        connection.moveMouse(dx, dy)
                                        lastPosition = position
                                        updateEdgeMotion(position)
                                    }
                                }
                            }
                            anyPressed = event.changes.any { it.pressed }
                        } while (anyPressed)
                        dragStarter.cancel()
                        edgeMotionJob?.cancel()
                        when {
                            dragging -> connection.mouseButton("left", "up")
                            multiTouch && !twoFingerMoved -> { connection.mouseButton("right", "press"); view.performHapticFeedback(HapticFeedbackConstants.KEYBOARD_TAP) }
                            !multiTouch && !moved -> { connection.mouseButton("left", "press"); view.performHapticFeedback(HapticFeedbackConstants.KEYBOARD_TAP) }
                        }
                    }
                }
            },
        contentAlignment = Alignment.Center
    ) {
        Text(
            "触控板\n单指移动 · 轻点左键 · 长按拖拽\n双指滚动 · 双指轻点右键",
            color = MaterialTheme.colorScheme.onSurfaceVariant,
            textAlign = TextAlign.Center,
            style = MaterialTheme.typography.bodyMedium,
            modifier = Modifier.padding(16.dp)
        )
    }
}

@Composable
private fun HoldBackspace(connection: VibeSocket, modifier: Modifier) {
    val interaction = remember { MutableInteractionSource() }
    val pressed by interaction.collectIsPressedAsState()
    LaunchedEffect(pressed) {
        if (pressed) {
            connection.key("BACKSPACE", "down")
            delay(400)
            var interval = 125L
            var elapsed = 0L
            while (pressed) {
                connection.key("BACKSPACE", "press")
                delay(interval)
                elapsed += interval
                if (elapsed >= 1600) interval = 67L
            }
        } else connection.key("BACKSPACE", "up")
    }
    Button(onClick = { }, interactionSource = interaction, modifier = modifier.height(42.dp)) {
        // 这个字形右侧较宽，做视觉居中校正，不影响按钮的实际点击区域。
        Text("⌫", modifier = Modifier.offset(x = (-4).dp))
    }
}
