# Windows work-screen workflow

This workflow targets the Windows development port. Use the existing native
computer-use tools; Yomado itself does not provide an agent, capture/input broker,
or independent keyboard and pointer. Confirm the installed app and driver are
usable before moving any application.

1. Verify the capture tool can select the non-primary target display or window.
   If it only captures the physical main screen, stop before creating/moving.
   Coordinate shared keyboard, pointer and focus with the desktop owner.
2. Open `Yomado.exe` or use its tray icon → `打开控制面板`. Reopening the app
   requests the existing panel. Inspect actual UI rather than fixed coordinates.
3. The header **新建屏幕** creates and starts a screen after configuration.
   Stopped profiles are listed below the overview with **启动** and **编辑**.
   Give this task's screen a meaningful name. Its **…** menu provides
   `重命名…` and `关闭虚拟屏`. Resolution is editable only while stopped.
4. Compare current display bounds, scale and physical-main state before moving
   any task windows. HiDPI means double physical pixels; read the actual Windows
   scale and fresh screenshot dimensions instead of assuming 200%.
5. Dragging an app icon selects only that app's ordinary windows on the source
   screen. Right-click provides `移入 …` / `返回原位置`. Dropping a running exe
   or shortcut from Explorer explicitly selects that app's ordinary windows
   across screens. Do not use a group containing unrelated user/task windows.
6. Before shared mouse/keyboard input, request a lease from the running app:
   `Yomado.exe --input request --agent Codex --reason "task action" --seconds 120`.
   Capture stdout with the tool's subprocess API, or pipe to `Out-String` in
   PowerShell. Proceed only after explicit user approval returns `granted`.
   Use `--input status --lease <leaseID>` before input; only `active` permits it.
   Release with `--input release --lease <leaseID>` when finished. A request
   times out after 60 seconds; a lease lasts 10–600 seconds and the physical
   screen shows a persistent stop button. Stop on any non-active response.
   This coordinates existing input tools; it does not isolate or inject input.
7. Select the actual work screen in the existing capture tool. Get a new target
   screenshot and perform one authorized, reversible input operation. Confirm
   the result in another screenshot. Screen inventory/preview alone is not
   input verification. Stop on blank capture, wrong-window input, or login checks.
8. Return only this task's moved windows and verify the restored geometry. The
   original position survives virtual-to-virtual transfers during this run.
   Return to another physical screen centers there; missing original displays
   fall back to an available physical screen. WeChat checkpoints favor its
   original physical screen; return it before another virtual transfer.
9. Close only the task-owned screen once all windows/work are accounted for.
   On uncertain recovery, keep the screen and report the pending recovery.
   If closing reports remaining dialogs, handle them or ask the user; do not
   approve the close-confirmation prompt on the user's behalf.
   Closing the panel leaves the app in the tray; `退出 yomado` restores all
   its screens and is not the operation for closing a single task screen.

`--diagnose <json-path>` writes local geometry/count diagnostics with hashed
display identifiers by default. `--preview-ui <png-path> light|dark` uses sample
data. Do not invent create/move RPCs, and do not enable physical-output changes,
alter trust/security policy or install a driver as routine task preparation.
Any required driver/certificate work needs explicit machine-owner authorization.
