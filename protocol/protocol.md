# VibePad wire protocol — v0

Transport: one WebSocket connection from Android to the Windows Agent at `ws://<computer-ip>:8765/vibepad/`. Each message is UTF-8 JSON.

## Client to Agent

### Paste text

```json
{"type":"paste_text","requestId":"uuid","text":"A complete prompt, including\nnewlines."}
```

The Agent writes `text` to the Windows clipboard and injects Ctrl+V into the currently focused application.

### Keyboard command

```json
{"type":"key","key":"ENTER","action":"press"}
{"type":"key","key":"BACKSPACE","action":"down"}
{"type":"key","key":"BACKSPACE","action":"up"}
```

Permitted keys in v0: `ENTER`, `BACKSPACE`, `SCREENSHOT`. `press` means an atomic key down/up pair. `SCREENSHOT` invokes the standard Windows screen-snipping shortcut (`Win`+`Shift`+`S`), so it does not depend on a laptop's manufacturer-specific function-key mapping.

### Mouse controls

```json
{"type":"mouse_move","dx":12,"dy":-5}
{"type":"mouse_button","button":"left","action":"down"}
{"type":"mouse_button","button":"left","action":"up"}
{"type":"mouse_button","button":"right","action":"press"}
{"type":"mouse_scroll","delta":-120}
```

`mouse_move` is relative movement. Permitted buttons are `left` and `right`; permitted actions are `down`, `up`, and `press`. A positive wheel `delta` scrolls up.

### Clipboard shortcuts

```json
{"type":"clipboard","action":"copy"}
{"type":"clipboard","action":"paste"}
```

These commands inject Ctrl+C and Ctrl+V respectively into the currently focused Windows application.

### Smart selection

```json
{"type":"selection_status"}
{"type":"smart_selection"}
```

The Android client periodically requests `selection_status`. When the focused Windows control exposes a non-empty text selection through Windows UI Automation, the client shows **提取** and `smart_selection` returns that text as `selection_result`, which is appended to the phone's input field. Otherwise it shows **全选** and `smart_selection` injects Ctrl+A.

## High-frequency mouse movement and scrolling

After the WebSocket connection is accepted, a current Agent returns `{"type":"udp_ready","port":8767,"scroll":true}`. Android then sends accumulated high-frequency input to UDP port `8767`.

| Input | UDP datagram |
| --- | --- |
| Mouse movement | 8 bytes, little-endian `float32 dx` followed by `float32 dy` |
| Two-finger scroll | 5 bytes, byte `0x01` followed by little-endian `int32 delta` |

Android batches both movement and scrolling at a stable 8ms cadence. The Agent accepts UDP only from the connected phone's IP address.

Android keeps emitting WebSocket fallbacks. The Agent ignores a fallback only after receiving the first valid UDP datagram of the corresponding kind, but continues to use WebSocket if UDP is blocked by a firewall. Older Agents omit `scroll:true`; Android then keeps scrolling over WebSocket only.

### Keepalive

```json
{"type":"ping","timestamp":1783900000000}
```

## Agent to client

```json
{"type":"paste_result","requestId":"uuid","success":true,"message":"pasted"}
{"type":"pong","timestamp":1783900000000}
{"type":"selection_state","hasSelection":true}
{"type":"selection_result","text":"Selected text"}
{"type":"selection_action","action":"select_all"}
{"type":"error","message":"Human-readable error"}
```

## Safety rules

- The Agent releases every held key when a socket disconnects, faults, or shuts down.
- The phone must send a `BACKSPACE` `up` event when its app enters the background.
- Commands are processed serially per connection; clipboard paste is never interleaved with another paste.
- The Agent releases held mouse buttons as well as held keyboard keys when a connection ends.
