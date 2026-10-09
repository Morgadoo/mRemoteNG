# mRemoteNG Cross-Platform Design System

This is the visual and interaction reference for `mRemoteNG.Avalonia`. It replaces the flat VS2015 look with a
modern, clean desktop design: calm neutral surfaces, one accent colour, rounded geometry, consistent spacing,
real icons, and keyboard-first workflows. Every view should follow it; when in doubt, choose less chrome.

## 1. Principles

1. **Content first.** Sessions (terminals, remote desktops) are the product. Chrome around them is quiet,
   compact and low-contrast; it never competes with the session.
2. **One accent.** The accent colour marks the primary action, the current selection and focus — nothing else.
   Protocol colours appear only in small icons and chips.
3. **Consistent rhythm.** 4 px grid; the same paddings, heights and radii everywhere.
4. **Keyboard first.** Everything reachable from the keyboard; every shortcut shown in a menu works; dialogs open
   with focus in the first field; Enter confirms, Escape cancels.
5. **Calm feedback.** Status is shown with small dots, icons and short messages, not modal interruptions.
6. **Both themes are first-class.** Every colour comes from a token; nothing is hard-coded per view.

## 2. Colour tokens

Token names are kept from the original palette (`AppBg0`…`Border1`) so user themes and the theme editor keep
working; the new tokens are additions. Each colour has a `…Brush` resource of the same name.

| Token | Role | Dark | Light |
|-------|------|------|-------|
| `AppBg0` | Window / session area background | `#14161B` | `#F5F6F8` |
| `AppBg1` | Sidebar, panels, header bar, menus | `#1A1D23` | `#FFFFFF` |
| `AppBg2` | Raised surfaces: inputs, cards, tab strip | `#21252D` | `#F0F2F5` |
| `AppBg3` | Selected row / active tab (neutral) | `#2A2F39` | `#E4E8EE` |
| `AppBg4` | Hover | `#252A33` | `#EBEEF2` |
| `TextPrimary` | Body text | `#E6E8EE` | `#1D2129` |
| `TextSecondary` | Labels, secondary text | `#A2A9B6` | `#4D5566` |
| `TextMuted` | Hints, placeholders, disabled | `#6B7385` | `#8A92A3` |
| `TextLink` | Links | `#7AA7FF` | `#2F6FED` |
| `Accent` | Primary buttons, focus, selection marker | `#4C8DFF` | `#2F6FED` |
| `AccentHover` | Primary hover | `#6AA0FF` | `#4A82F0` |
| `AccentActive` | Primary pressed | `#3A78E6` | `#2459C8` |
| `Border0` | Default borders / dividers | `#2B303A` | `#E1E5EB` |
| `Border1` | Strong borders (inputs, popups) | `#363C48` | `#CDD3DC` |
| `AccentSubtle` *(new)* | Selected tree row / list item background | `#4C8DFF` @ 18 % | `#2F6FED` @ 12 % |
| `OnAccent` *(new)* | Text/icons on accent | `#FFFFFF` | `#FFFFFF` |
| `Success` *(new)* | Connected, OK | `#3FB950` | `#1F8F3A` |
| `Warning` *(new)* | Warnings, reconnecting | `#D29922` | `#B7791F` |
| `Danger` *(new)* | Errors, destructive actions | `#F85149` | `#D1242F` |
| `Overlay` *(new)* | Modal scrim | `#000000` @ 45 % | `#0F172A` @ 25 % |

Protocol colours (icons and chips only; same in both themes, chips use the colour at 16–20 % as background and
the full colour as text): SSH `#3FB950`, Telnet/Rlogin/Raw `#8B949E`, RDP `#4C8DFF`, VNC/ARD `#A371F7`,
HTTP/HTTPS `#F0883E`, PowerShell `#56B6F7`, Terminal/WSL `#C9D1D9` (light: `#57606A`), Serial `#D29922`,
IntApp/External `#DB61A2`, AnyDesk `#EF443B`.

The built-in derived themes (VS2015 Blue, Darcula) and user themes override the same keys.

## 3. Typography

- UI font: **Inter** (embedded via `Avalonia.Fonts.Inter`), falling back to the system UI font.
- Monospace (terminals, hostnames in technical fields, log): `JetBrains Mono, Cascadia Mono, DejaVu Sans Mono,
  Consolas, monospace` — the terminal keeps its own font setting.

| Style class | Size / weight | Use |
|-------------|---------------|-----|
| `h1` | 20 / SemiBold | Dialog and page titles |
| `h2` | 15 / SemiBold | Section titles inside pages and cards |
| (default) | 13 / Regular | Body, controls |
| `caption` | 12 / Regular, `TextSecondary` | Descriptions, helper text |
| `overline` | 11 / SemiBold, `TextMuted`, letter-spacing 0.6 | Sidebar/panel headers ("CONNECTIONS") |
| `mono` | 12.5 monospace | Hostnames, ports, paths, log lines |

## 4. Geometry and spacing

- Spacing scale: 4, 8, 12, 16, 24, 32. Dialog content padding 24; page padding 24; card padding 16;
  toolbar/header padding 8 horizontal.
- Corner radius: **4** chips, checkboxes, scrollbar thumbs; **6** buttons, inputs, combo boxes, tree/list rows,
  tabs; **8** cards, menus, popups, tooltips; **10** dialogs/floating panels.
- Control heights: 32 default (buttons, inputs, combos), 28 compact (header bar, toolbars, tree rows, menu
  items), 24 status bar.
- Borders 1 px; focus ring 2 px `Accent` (outside the control, radius + 2).
- Elevation: menus, popups, tooltips, toasts and dialogs get a soft shadow (`0 8 24 0 #000000 @ 35 %` dark,
  `@ 12 %` light) and a `Border1` outline. Nothing else has shadows.

## 5. Iconography

- **Material Design Icons** via `Material.Icons.Avalonia` (`<mi:MaterialIcon Kind="…"/>`), 16 px in rows,
  menus and toolbars, 20 px in the header bar, 48–64 px in empty states. Icons inherit the text colour unless
  they carry protocol meaning.
- Protocol → icon: SSH `Console`, Telnet `ConsoleNetwork`, Rlogin `ConsoleLine`, Raw `SerialPort`/`LanConnect`,
  RDP `MonitorScreenshot` (or `RemoteDesktop`), VNC/ARD `MonitorEye`, HTTP/HTTPS `Web`, PowerShell `Powershell`,
  Terminal `Console`, WSL `Linux`, Serial `SerialPort`, IntApp `Application`, AnyDesk `Monitor`; folder
  `Folder`/`FolderOpen`; root `Database`; PuTTY sessions root `ConsoleNetworkOutline`.
- A connection's legacy `Icon` name (e.g. "Linux", "Windows", "Database") maps to a matching Material glyph when
  one exists; otherwise the protocol glyph is used. The default "mRemoteNG" icon always uses the protocol glyph.
- Menus show icons for the most common items only (New, Open, Save, Connect, Options, Exit…), never for all.

## 6. Components

### Buttons
- **Primary** (`Classes="accent"`): `Accent` background, `OnAccent` text, radius 6, height 32, padding 14×0.
  At most one per view (the default action).
- **Secondary** (default `Button`): `AppBg2` background, `Border1` border, `TextPrimary`; hover `AppBg4`;
  pressed `AppBg3`. Never turns accent on press.
- **Subtle / ghost** (`Classes="subtle"`): transparent, no border; hover `AppBg4`. Used in toolbars and headers.
- **Icon button** (`Classes="icon"`): 28×28 (header bar 32×32), ghost style, icon only, always has a tooltip
  that includes the shortcut ("New connection (Ctrl+N)").
- **Danger** (`Classes="danger"`): `Danger` text on transparent; primary-danger variant with `Danger` fill for
  confirmation dialogs only.
- Disabled: 45 % opacity, no hover.

### Inputs
- TextBox/ComboBox/NumericUpDown: height 32, `AppBg2` fill, `Border1` border, radius 6, padding 10×6;
  hover border `TextMuted`; focus border `Accent` + focus ring; placeholder `TextMuted`; error border `Danger`
  with a `caption` message below in `Danger`.
- Search fields (`Classes="search"`): leading magnifier icon, trailing clear (×) button when non-empty,
  radius 6, height 30.
- CheckBox/RadioButton: 18 px box, radius 4, accent fill when checked. ToggleSwitch for on/off settings.

### Lists, tree, menus
- Rows: height 28, radius 6, inset 4 px from the container edges; hover `AppBg4`; selected `AccentSubtle`
  with a 2 px `Accent` bar on the left edge and `TextPrimary` text (not white-on-accent).
- Menus and context menus: `AppBg1`, `Border1` outline, radius 8, shadow, 4 px inner padding; items height 28,
  radius 6, padding 10×0; shortcut text right-aligned in `TextMuted`; separators `Border0` with 4 px margin.
- Tooltips: `AppBg2` (dark) / `#1D2129` with white text (light), radius 6, padding 8×5, 12 px text.

### Tabs (session tabs)
- Tab strip on `AppBg1` with a `Border0` bottom divider; tabs height 34, radius 6 6 0 0, padding 12×0, gap 2.
- Tab content: protocol icon (16, protocol colour) · title (13, `TextSecondary`; active `TextPrimary`) ·
  status dot (6 px: Success connected, Warning connecting/reconnecting, Danger error, none disconnected) ·
  close button (visible on hover and on the active tab).
- Active tab: `AppBg0` background (merges with the session area) and a 2 px accent line on top. Tab colour
  (`TabColor`) tints the top line and icon instead of the whole tab.
- Overflow: horizontal scroll with fade edges plus a "⌄" button listing all tabs.

### Panels and splitters
- Sidebar (connection tree) on `AppBg1`, separated by a 1 px `Border0` line. Splitters are invisible (4 px hit
  area) and show an `Accent` 2 px line on hover/drag.
- Panel headers: height 36, `overline` title on the left, icon buttons on the right.
- Bottom panel (Log/Terminal/Debug): collapsible, header with icon tabs; log rows show a level icon
  (info `InformationOutline` TextMuted, warning `AlertOutline` Warning, error `AlertCircleOutline` Danger),
  timestamp in `mono`/`TextMuted`.

### Cards and settings
- Card (`Border.card`): `AppBg1` (light: white) with `Border0` outline, radius 8, padding 16.
- Settings rows (Options, connection editor): label (13 `TextPrimary`) + description (`caption`) on the left,
  control on the right (min width 220) or below for wide controls; rows separated by `Border0` dividers inside a
  card; groups titled with `h2`.

### Dialogs
- Radius 10 (where the platform allows), `AppBg1` background, content padding 24.
- Header: optional 20 px icon + `h1` title + `caption` subtitle.
- Footer: right-aligned actions, primary last (rightmost), 8 px gap, separated by a `Border0` line on
  `AppBg0`/`AppBg2` footer band, height 56.
- Open with focus in the first input; Enter = default button; Escape = cancel. Never larger than needed;
  resizable when content can grow.

### Status bar
- Height 24, `AppBg1`, top `Border0` divider, 12 px text `TextSecondary`.
- Left: session count with a Success dot when > 0; current file (file name only, full path in tooltip, unsaved
  dot). Right: notifications/log indicator, theme toggle, version.

### Empty states
- Centered: 56 px icon in `TextMuted`, `h2` title, `caption` text, a primary and a secondary action, and —
  where useful — shortcuts as keycap chips (`Ctrl` `K`).
- The session area's empty state shows: "No open sessions", actions "Quick connect" (primary) and "New
  connection", and a grid of up to 8 **Favorites/Recent** connection cards (icon, name, host, protocol chip;
  click = connect).

### Toasts
- Bottom-right, `AppBg1` card with shadow, icon by level, title + message, auto-dismiss 5 s (errors stay until
  closed), max 3 stacked. Used for connection errors, saves, update notices — the log keeps the full record.

## 7. Layout of the main window

```
┌ Menu bar (File View Sessions Favorites Tools Help) ─────────────────────────────────┐
├ Header bar (h 44): [+ New ▾] [Open] [Save] │ ⌕ Quick connect: host[:port]  [SSH ▾] (Connect) │ ext tools… │ ◐ ⚙ │
├──────────────┬──────────────────────────────────────────────────────────────────────┤
│ CONNECTIONS  │ ▣ ssh-demo ● ×  ▣ rdp ●  ▣ vnc                                  ⌄ │
│ [⌕ Search  ] ├──────────────────────────────────────────────────────────────────────┤
│ ▾ Linux lab  │                                                                      │
│   ▣ ssh  SSH │                         session content                              │
│   ▣ rdp  RDP │                                                                      │
│              ├──────────────────────────────────────────────────────────────────────┤
│              │ ▸ Log  Terminal  Debug  (collapsible)                                │
├──────────────┴──────────────────────────────────────────────────────────────────────┤
│ ● 3 sessions · confCons.xml                                     ◐  v1.78.2-dev     │
└─────────────────────────────────────────────────────────────────────────────────────┘
```

## 8. Interaction rules

- **Command palette** (Ctrl+K, also Ctrl+Shift+P): one search box over connections (fuzzy by name, host,
  folder, tags; Enter connects, Ctrl+Enter connects with options) and commands (every menu command, with its
  shortcut). Recent items first.
- Every `InputGesture` shown in a menu is also a working `KeyBinding` on the window.
- Tree: double-click / Enter connects; F2 renames; Ctrl+E edits; hover shows a "connect" icon button on the row;
  rows with open sessions show a Success dot.
- Destructive actions confirm with a dialog whose primary button is the danger variant and names the action
  ("Delete 3 connections").
- Long operations (port scan, import, SQL load) show inline progress, never a frozen UI.
- Motion: 120–160 ms ease-out for hover/press colour changes and popups; no motion on session content.

## 9. Implementation reference

Where the vocabulary above lives in `mRemoteNG.Avalonia` (see the design gallery: run with `--design-gallery`).

- **Palettes** `Themes/DarkTheme.axaml`, `Themes/LightTheme.axaml`: colours only. Every §2 token is a `Color` plus a
  `…Brush` (`AppBg0Brush`, `AccentSubtleBrush`, `ProtoSshBrush`…); `SuccessTintBrush`, `WarningTintBrush`,
  `DangerTintBrush` and `Proto…TintBrush` are the same colours at 16–18 % for status/chip backgrounds;
  `PopupShadow` and `FocusRingShadow` are `BoxShadows`; `ToolTipBackground/Foreground/BorderBrush` style tooltips.
  Always reference them with `{DynamicResource …}` so theme switches and the theme editor's live preview apply.
- **Control styles** `Themes/Controls.axaml` (included once in `App.axaml`). Fluent's state brushes are aliased to
  the palette by `Services/ThemeTokens.cs`, so new styles only need geometry. Style classes:
  - `TextBlock`: `h1`, `h2`, `caption`, `overline`, `mono`, `section-header` (legacy), `muted`, `secondary`,
    `success`, `warning`, `danger`, `link`. `TextBox`: `search`, `mono`, `error`.
  - `Button`: default (secondary), `accent`, `subtle`, `icon` (28 × 28; put a 16 px `mi:MaterialIcon` inside),
    `danger`, `accent danger` (filled, confirmations only), `link`. `ToggleButton`: `subtle`, `icon` (checked =
    `AccentSubtle` + accent glyph).
  - `Border`: `card`, `panel-header`, `toolbar`, `statusbar`, `dialog-footer`, `badge`, `chip`, `keycap`,
    `dot` (+ `success`/`warning`/`danger`), `divider`, `overlay`, `elevated` (popover/toast surface with shadow).
  - `Window.dialog`: `AppBg1` dialog background.
  - Lists and trees get the 28 px rounded rows with the accent bar automatically; `ListBox.session-tabs` opts out.
- **Fonts**: `UiFontFamily` (Inter, from `Avalonia.Fonts.Inter`), `MonoFontFamily`, `UiFontSize`; Options >
  Appearance overrides `UiFontFamily`/`UiFontSize` through `ThemeService`.
- **Icons**: `xmlns:mi="using:Material.Icons.Avalonia"`, `<mi:MaterialIcon Kind="Plus"/>` (16 px by default).
  `Services/ProtocolVisuals.cs` maps protocols and connections to glyphs, labels and theme-following brushes; XAML
  uses `xmlns:conv="using:mRemoteNG.Avalonia.Converters"`:
  `{Binding Protocol, Converter={x:Static conv:ProtocolConverters.Icon}}` (also `.Brush`, `.TintBrush`, `.Label`),
  `{Binding Converter={x:Static conv:ConnectionConverters.Icon}}` (also `.Brush`, and the multi-value
  `.IconExpanded` taking the node and its `IsExpanded`).
- **Controls**: `xmlns:ctl="using:mRemoteNG.Avalonia.Controls"`, `<ctl:ProtocolChip Protocol="{Binding Protocol}"/>`;
  `TextBox.search` clears through `ctl:SearchBox.ClearCommand`.
