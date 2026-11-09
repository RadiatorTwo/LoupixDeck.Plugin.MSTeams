# Teams UI Automation – measurement notes

Findings from probing the **new Microsoft Teams desktop client** (`ms-teams.exe`, WebView2) via
UI Automation. Background: the local Teams third-party app API (`ws://localhost:8124`) was retired by
Microsoft on **2026-06-30** (message center MC1266901) without a replacement, so UI Automation is the
only way left to control the local client with live state. Microsoft Graph cannot control the local
client (its `call: mute` only works for calls owned by a bot application).

- **Measured:** 2026-11-09, Teams `26276.513.5241.9675`, UI language **German**, Windows 11 24H2
  (10.0.26100), personal Microsoft account joined as **guest** into a "Meet now" meeting.
- **Probe tool:** FlaUI.UIA3 on .NET 9 (read-only dump/watch tool, not part of this repo).
- **Reference implementation:** [danswett/streamdeck-teams-control](https://github.com/danswett/streamdeck-teams-control)
  (MIT) – C# sidecar using FlaUI; its `selectors.json` matches these findings.

## Finding the meeting window

- Process `ms-teams`; top-level window class `TeamsWebView`, title e.g. `Besprechung mit … | Microsoft Teams`.
- A window is the meeting window if its subtree contains AutomationId `microphone-button`
  (do not rely on the title – it is localized and contains the meeting name).
- Chromium only builds its accessibility tree on demand: send `WM_GETOBJECT` (0x003D, `OBJID_CLIENT` = -4)
  via `SendMessageTimeout` (`SMTO_ABORTIFHUNG`, ~250 ms) to the top-level window **and all child windows**
  (incl. `Chrome_RenderWidgetHostHWND`). Repeat periodically; Chromium drops the tree again when unused.
- The Teams web app in a browser (tested: Thorium/Chromium) did **not** expose the page content via UIA
  with the same nudge – only the browser chrome. Browser/PWA support is not covered.

## Main toolbar (meeting window)

All toolbar buttons are `ControlType.Button`, `AriaRole=button`, support `Invoke`. **No `aria-pressed`,
no `TogglePattern`** on the toggles – state is only available through `Name` / `FullDescription` text,
which is **localized**. `ClassName` is a generated Fluent UI CSS-hash list – do not use it.

| AutomationId | Name (de) | FullDescription (de) | Notes |
|---|---|---|---|
| `microphone-button` | `Mikrofon stummschalten` → mic **on**; `Mikrofon wieder aktivieren` → mic **muted** | – | |
| `video-button` | `Kamera aktivieren` → camera **off**; `Kamera ausschalten` → camera **on** | when no camera permission: `Erlauben Sie Teams den Zugriff auf Ihre Kamera…`, `IsEnabled=false`, aria `disabled=true` | |
| `video-button-configure` | `Videooptionen öffnen` | – | menu button (`ExpandCollapse`), opens video options / background effects |
| `audio-button-configure` | `Audiooptionen öffnen` | – | menu button |
| `raisehands-button` | `Heben` (constant!) | `Heben Sie Ihre Hand` → hand **down**; `Senken Sie Ihre Hand` → hand **up** | has `ExpandCollapse`; state only via FullDescription |
| `reaction-menu-button` | `Reagieren` | `Reaktion senden` | opens reactions flyout |
| `share-button` | `Teilen` → not sharing; `Teilen beenden` → **sharing** | `Inhalt freigeben` / `Nicht mehr teilen` | opens share tray when not sharing |
| `hangup-button` | `Verlassen` | – | leaves immediately |
| `chat-button`, `roster-button`, `view-mode-button`, `recording-button`, `callingButtons-showMoreBtn`, `more-options-header`, `e2ee-status` | | | `callingButtons-showMoreBtn` only exists in the full (non-compact) toolbar |

## Reactions flyout (`reaction-menu-button`)

Stable, language-independent ids (Buttons): `like-button` (Gefällt mir), `heart-button` (Liebe),
`applause-button` (Applaus), `laugh-button` (Lachen), `surprised-button` (Überrascht).

## Video options flyout (`video-button-configure`)

- Camera list: `ListItem`, AutomationId = device path / moniker (e.g. `\\?\usb#vid_3564&pid_fefb…`,
  `@device:sw:{860BB310-…}\{A3FCE0F5-…}`), `Name` = device name, `SelectionItem.IsSelected`.
- Background effects: `Menu` named `Hintergründe` containing `CheckBox` items with **`TogglePattern`
  and aria `checked=true/false`** – readable state, but only while the flyout is open:
  - `Kein Hintergrundeffekt` (no effect), `Standardunschärfe` (standard blur), plus custom backgrounds.
  - AutomationIds like `menur99` / `menur9r` are **regenerated on every open** → match by Name.
- Switches (`Button`, aria `pressed`, TogglePattern): `Helligkeit anpassen`, `Weicher Fokus` – ids like
  `switch-r9k` are regenerated → match by Name.
- `Weitere Videoeffekte und Einstellungen` opens the side panel.

## Share tray (`share-button` while not sharing)

- `switch-r…` `Sound einschließen` (include sound, aria `pressed`).
- Layout radio buttons (no ids): `Nur Inhalt`, `Inhalt im Hintergrund`, `Nebeneinander`, `Reporter`.
- Screens: `ListItem` without AutomationId, Name = monitor name (e.g. `Generic PnP Monitor`).
- Windows: `ListItem` `Öffnet eine Liste der N aktiven Fenster` (localized).
- Selecting a monitor ListItem starts sharing immediately.

## While sharing

- Teams switches to a compact presenter bar/window. `share-button` stays reachable
  (`Teilen beenden` / `Nicht mehr teilen`).
- Additional buttons: `give-control-button`, `share-system-audio-button`, `optimise-video-content`,
  `apl-mode-switcher-button`, `Minimize-Restore`.

## Behaviour to handle

- **Flyouts hide the toolbar:** while the share tray / reactions / raise-hand menu is open, *all*
  toolbar elements disappear from the UIA tree for ~0.5–1 s. Do not treat this as "meeting ended" –
  use a grace period.
- Opening a menu sets aria `expanded=true` on its button (`ExpandCollapseState`).
- After a toggle, the new `Name` is visible within one 300 ms poll.
- Camera/mic permission: if Windows denies Teams camera access
  (`HKCU\…\CapabilityAccessManager\ConsentStore\webcam\MSTeams_8wekyb3d8bbwe = Deny`), `video-button`
  is disabled with an explanatory FullDescription – surface that as "unavailable".

## Findings from the plugin smoke test (2026-11-09)

- **Raise hand is a plain toggle.** Clicking `raisehands-button` raises/lowers the hand directly.
  Its aria `expanded` (ExpandCollapse state) is `true` while the hand is up – a **language-independent**
  state signal, used before the FullDescription patterns.
- **Posted clicks work:** `WM_MOUSEMOVE/LBUTTONDOWN/LBUTTONUP` posted to the
  `Chrome_RenderWidgetHostHWND` under the element toggle mic/camera/hand/share and open the
  reaction/video/share flyouts; the new label is readable ~5 ms after the click returns.
- **Sharing moves the toolbar:** while sharing, the meeting window is **minimized** and two extra
  windows appear: `Kompakte Besprechungsansicht | …` (compact meeting view with mic/camera/share,
  **no** `reaction-menu-button`, no `callingButtons-showMoreBtn`) and `Freigabesteuerungsleiste | Microsoft Teams`
  (share control bar, has `share-button`). Clicking into the minimized window does nothing and its
  labels may be stale – always prefer a non-minimized window with the toolbar.
- After stopping the share, the compact window disappears and the meeting window is restored.
- Reactions are not reachable while sharing (only the minimized meeting window has the button).
- Camera toggle: `video-button` is briefly `disabled=true` while the device switches.

## Open points

- English (and other) UI strings not yet measured on this machine – take them from the reference
  project's `selectors.json` / locale packs and verify.
- Behaviour with a work/school account and in the compact meeting view not yet measured.
- Raise hand: confirm that invoking `raisehands-button` toggles directly (observed `expanded=true`
  once and a `Weitere Optionen` menu item appearing).
