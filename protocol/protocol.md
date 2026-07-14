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

Permitted keys in v0: `ENTER`, `BACKSPACE`. `press` means an atomic key down/up pair.

### Mouse controls

```json
{"type":"mouse_move","dx":12,"dy":-5}
{"type":"mouse_button","button":"left","action":"down"}
{"type":"mouse_button","button":"left","action":"up"}
{"type":"mouse_button","button":"right","action":"press"}
{"type":"mouse_scroll","delta":-120}
```

`mouse_move` is relative movement. Permitted buttons are `left` and `right`; permitted actions are `down`, `up`, and `press`. A positive wheel `delta` scrolls up.

### Keepalive

```json
{"type":"ping","timestamp":1783900000000}
```

## Agent to client

```json
{"type":"paste_result","requestId":"uuid","success":true,"message":"pasted"}
{"type":"pong","timestamp":1783900000000}
{"type":"error","message":"Human-readable error"}
```

## Safety rules

- The Agent releases every held key when a socket disconnects, faults, or shuts down.
- The phone must send a `BACKSPACE` `up` event when its app enters the background.
- Commands are processed serially per connection; clipboard paste is never interleaved with another paste.
- The Agent releases held mouse buttons as well as held keyboard keys when a connection ends.
