# Epsilon visual system for Maia Translator

This guide describes the visual language currently used by Epsilon Download Manager. Use the same tokens and component rules in Maia Translator, but adapt the layout to translation work rather than copying Epsilon's download-manager screens.

## Design direction

- Premium, compact, calm, and information-dense.
- Layered surfaces instead of one large coloured title bar.
- Accent colour is used for focus, selection, progress, and primary actions—not as a large background.
- Small-radius rectangles are the default. Avoid pill-shaped or oval controls except true status chips.
- Icons are clean line icons with selective colour. Avoid circles around every toolbar icon.
- Every colour comes from a semantic theme token so all open windows can update immediately.
- Dark themes must keep high text contrast; secondary information uses `Muted`, never a dark blue hard-coded colour.

## Typography

| Use | Font | Size / weight |
|---|---|---|
| Application UI | Segoe UI Variable Text, Segoe UI, Inter | 13 px regular |
| Window/page title | Segoe UI Variable Display, Segoe UI | 17 px semibold |
| Item/file title | Segoe UI Variable Text | 14 px semibold |
| Section label | Segoe UI Variable Text | 11 px bold |
| Metadata/helper text | Segoe UI Variable Text | 11.5–12.5 px regular |
| Speed, counters, technical values | Cascadia Mono, Consolas, Courier New | 13 px |
| Windows glyph fallback | Segoe MDL2 Assets | as required |

For Maia, use the display face for language names and document titles, the UI face for controls, and the mono face only for character counts, timing, model statistics, or diagnostic data.

## Semantic colour tokens

The exact values for every theme are in `epsilon-theme-tokens.json`.

| Token | Purpose |
|---|---|
| `Bg` | Outer window background |
| `Panel` | Primary content panels |
| `Panel2` | Secondary/raised panel layer |
| `Surface` | Cards, rows, and control surfaces |
| `Border` | Dividers and neutral outlines |
| `Text` | Primary readable text |
| `Muted` | Secondary text and metadata |
| `Faint` | Tertiary and decorative text |
| `TopBarText` | Text on the title/header area |
| `Accent` | Primary action, focus, selection, progress |
| `AccentDark` | Hover/pressed accent and strong outline |
| `OnAccent` | Text or icon placed on accent |
| `Hover` | Neutral hover background |
| `Selected` | Selected row/background |
| `Success`, `Danger`, `Warning` | Semantic states |
| `SuccessBg` | Low-emphasis success surface |
| `HeaderBg` | Toolbar/footer/header layer |
| `Track` | Empty progress track |
| `Input`, `InputBorder` | Text fields and editable controls |
| `Popup` | Menus, dialogs, flyouts |
| `IconGrey`, `IconPurple`, `IconTeal`, `IconAmber` | Secondary icon accents |
| `ScrollThumb`, `ScrollThumbHover` | Scrollbar states |
| `Disabled` | Disabled foreground |

## Theme catalogue

1. **Obsidian Gold** — near-black luxury base with restrained warm gold.
2. **Platinum Blue** — bright professional blue; the default light theme.
3. **Royal Amethyst** — deep violet, rich and expressive.
4. **Emerald Executive** — dark green, confident and technical.
5. **Champagne Minimal** — warm ivory and bronze; the sole warm-light theme.
6. **Graphite Copper** — industrial charcoal with copper accents.
7. **Sapphire Noir** — deep navy with electric blue; the default dark theme.
8. **Rose Titanium** — dark rose and muted titanium tones.
9. **Arctic Glass** — cool dark cyan/glass-inspired palette.
10. **Crystal Noir** — translucent dark luxury palette; uses Windows acrylic where available.
11. **Dracula** — familiar dark purple/pink/cyan coding palette.
12. **Makan Lab** — black/navy brand theme with blue, cyan, and green highlights.

`Ivory Luxe` is a retired duplicate and must resolve to `Champagne Minimal`. `Auto` resolves to Sapphire Noir when Windows is dark and Platinum Blue otherwise.

## Brand colours

- Main brand gradient: `#39F5B0` → `#38D9FF` → `#4D7CFF`.
- Connection/online gradient: `#4D7CFF` → `#38D9FF`.
- These gradients are shared branding accents. Use them sparingly for a logo mark, active connection indicator, or premium highlight—not for whole window backgrounds.

## Geometry and spacing

- Base spacing rhythm: 4, 6, 8, 10, 12, 16, 20, 24 px.
- Standard button: horizontal/vertical padding `16 × 7`, corner radius `4`, border `1`.
- Text input: padding `9 × 6`, corner radius `7`, border `1`, minimum height `32`.
- Card: corner radius `6`; large grouped card: `10`.
- Menu row: corner radius `6`, padding `8–12 × 6–9`.
- Popup/dialog: corner radius `10`, border `1`, padding `16–20`.
- Status chip: corner radius `11` only when it contains a status dot and label.
- Toolbar icon: normally `21 × 21`; caption around `11–12` px.
- Scrollbar: `10` px; thumb radius `4`, internal margin `2`.
- Progress/segment bar: `6–7` px high and rectangular. Do not use pill ends.
- Snap borders and strokes to device pixels for crisp rendering.

## Core controls

### Buttons

- Neutral: transparent or `Surface`, `Border`, `Text`.
- Hover: `Hover` background with `AccentDark` outline.
- Pressed: `Selected` background.
- Focus: `Accent` outline.
- Disabled: opacity `0.45` and `Disabled` foreground.
- Primary: `Accent` fill, `OnAccent` text, semibold; hover opacity `0.90`, pressed `0.75`.
- Destructive: `Danger` icon/text; only fill the button red for the final destructive confirmation.

### Inputs

- Background `Input`, outline `InputBorder`, text `Text`, caret/focus `Accent`.
- Combo boxes use radius `7`; their popup uses `Popup`, radius `9`, 1 px border, and a soft shadow.
- Checkbox is `18 × 18`, radius `5`, border `1.5`; checked state uses `Accent` and `OnAccent`.
- Epsilon's option selector is an 18 px compact square indicator. For Maia, use standard circular radio controls where the choice is mutually exclusive.

### Cards, tabs, and navigation

- Cards use `Surface`, `Border`, radius `6` or `10`.
- Tabs use padding `18 × 9`, top radius `8`, and accent the selected tab.
- A settings side-tab row uses padding `12 × 9`, radius `6`, and a 3 px accent marker.
- Avoid a permanent heavy sidebar unless Maia genuinely needs it. Translation history may use a collapsible rail.

### Menus and dialogs

- Use `Popup` background, `Border`, radius `10`, padding `6` inside menus.
- Typical shadow: blur `16–18`, depth `4–5`, opacity `0.35–0.42`.
- Style context menus and native selection colours with the theme tokens; never leave unreadable default Windows menu colours.
- Dialog title `17` semibold; body `13`; helper text `11`; actions aligned consistently.

## Status language

| State | Token |
|---|---|
| Working / translating / downloading | `Accent` |
| Waiting / queued | `IconPurple` |
| Completed | `Success` |
| Failed | `Danger` |
| Paused / attention | `Warning` |
| Network-connected | `IconTeal` or brand connection gradient |

For Maia, add translator-specific states: Detecting = `IconPurple`, Translating = `Accent`, Completed = `Success`, Needs review = `Warning`, Failed = `Danger`.

## Window behaviour

- Native title bars switch between dark and light mode to match the selected theme.
- `Crystal Noir` can use Windows 11 acrylic/transient backdrop. Other themes use opaque layered surfaces.
- Glass effects must retain an opaque fallback and readable contrast.
- Theme changes should repaint all open dialogs, popups, context menus, and secondary windows immediately.
- Keep a minimum contrast ratio near WCAG AA: 4.5:1 for normal text and 3:1 for large text and essential icons.

## Recommended Maia screen structure

1. Compact title/menu bar with the Maia mark, connection/model state, theme, and settings.
2. A quiet action toolbar for New, Open, Paste, Translate, Stop, History, and Export.
3. Two equal editor cards for source and translation, each with a language header and character count.
4. A narrow central action rail for swap and translate—not a large ribbon.
5. A bottom status strip for model/provider, latency, document count, and privacy state.
6. Popups for glossary, model selection, and export should use the same radius-10 premium popup style.

## Do not copy from Epsilon

- Download-specific segmented connection bars, queue badges, torrent state, and speed counters do not belong in Maia.
- Do not use a large coloured bar across the top.
- Do not wrap every icon in a circle.
- Do not add oval controls merely to look modern.
- Do not hard-code theme colours inside individual windows; always bind to semantic tokens.
