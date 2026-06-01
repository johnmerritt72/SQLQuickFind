# SQLQuickFind — Themed autocomplete dropdown

**Date:** 2026-06-01
**Status:** Approved (design)

## Problem

The toolbar autocomplete results popup is built in code in
[`ToolbarAutocomplete.CreatePopup`](../../../src/SQLQuickFind.Vsix/Services/ToolbarAutocomplete.cs)
using `SystemColors.*` brushes (`WindowBrush`, `ActiveBorderBrush`). In SSMS's dark
theme this renders as a near-white Windows dropdown with square corners — visibly
foreign next to native SSMS UI and third-party extensions (e.g. dbForge Search, which
shows a dark themed list with rounded corners).

## Goal

Make the results/history dropdown match the active SSMS theme: themed background, item
text, selection and hover highlight, themed border, and rounded corners. Adapt
automatically to dark / light / blue themes, including live theme switches.

**Out of scope:** the popup scrollbar (left default), and the toolbar search textbox
itself (it is the native VS combo and already themed).

## Approach

**Snapshot live theme colors in code** (chosen over `DynamicResource`/`SetResourceReference`).

The popup is hosted in its own top-level HWND because `Popup.AllowsTransparency = true`.
VS theme resources live in the main window's resource tree and frequently fail to resolve
across that HWND boundary, so resource-reference theming tends to render unstyled here.
Reading concrete colors via `VSColorTheme.GetThemedColor(EnvironmentColors.*)` and building
`SolidColorBrush`es is the reliable path, and reading them live makes the result correct in
every theme.

## Design

### Theme brush resolution
A small helper (e.g. `ThemeBrushes` within `ToolbarAutocomplete`) resolves WPF brushes from
`EnvironmentColors` keys, each with a fallback key and a final hard-coded default so a
missing key never throws or yields an invisible (transparent) brush. Brushes needed:

- Popup background
- Item foreground (text)
- Border
- Selected-item background / foreground
- Hover (mouse-over) background / foreground

`GetThemedColor` returns `System.Drawing.Color`; convert to `System.Windows.Media.Color`
and wrap in a `SolidColorBrush` (frozen).

### CreatePopup changes
- **Border:** themed `Background` + themed `BorderBrush`, `CornerRadius = 5`, modest
  `Padding` so rows don't touch the rounded edge. The outer `Popup` remains transparent so
  the rounded corners render without a square backing.
- **ListBox:** themed `Background`/`Foreground`, `BorderThickness = 0` (the `Border` draws
  the frame).
- **ItemContainerStyle:** add a `ControlTemplate` with `IsMouseOver` and `IsSelected`
  triggers driven by the themed hover/selected brushes. **The existing
  `MouseLeftButtonUp` and `PreviewMouseLeftButtonDown` `EventSetter`s must be preserved** —
  they carry the click-to-open behavior (commit-on-mouse-down) that was just fixed; the new
  template must not regress it.

### Live theme switching
Subscribe to `VSColorTheme.ThemeChanged`; on change, rebuild the brushes and reapply them to
the existing Border/ListBox/item style. Handlers run on the UI thread. Unsubscribe is
non-critical given the static, process-lifetime ownership of these elements.

## Risks / notes
- Exact `EnvironmentColors` key names vary; the fallback-chain helper absorbs any
  unavailable key. Verify chosen keys at implementation time.
- Must not disturb the popup open/close and commit logic recently fixed (the `_committing`
  guard and `OpenPopup`/`CanShowPopup` focus gating).

## Verification
In-SSMS WPF — no meaningful unit test. Verify by: bump version → Clean build → install →
visually confirm in the dark theme that the dropdown background, text, hover/selection, and
rounded corners match SSMS; toggle to a light theme once to confirm adaptivity. Confirm
clicking a result still opens it and the dropdown still dismisses (no regression of the
prior fix).
