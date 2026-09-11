# In-game config UI

A widget kit for building config panels out of Jotunn's `GUIManager` primitives.

## Dependency rule

**Nothing in this folder may reference `YamlConfigFile`, `YamlConfigManager`, `ConfigNetwork` or
`ValidationReport`.** Its only in-repo dependency is `Logger`.

That is not tidiness — it is what lets a mod with a completely different config system take this folder
without also swallowing the YAML framework next door. Keep it true.

## Widgets

Layout is **top-left origin**: `anchorMin = anchorMax = pivot = (0,1)`, `anchoredPosition = (x, -y)`.
Build a `List<GameObject>` of rows in reading order, then call `LayoutColumn(rows, x, startY)` once. It
skips inactive rows, so `SetActive(false)` on a conditional row collapses its space — re-run it after any
visibility change to reflow.

| Widget | Use for |
| --- | --- |
| `AddToggleRow` | bool |
| `AddSliderRow` | int/float — slider plus a typed box, bound both ways and clamped |
| `AddEnumCycleRow` | an enum with **≤ 6** members |
| `AddPickerRow` | an enum with more, or any open-ended name (prefabs) |
| `AddEnumFlagsRow` | a small enum used as a set |
| `AddTextFieldRow` | free text |
| `AddStringListEditor` | `List<string>` with add/remove |

`AddCloseX(panel, panelWidth, onClick)` puts a dismiss button in a panel's top-right corner. Prefer it to
a full-width "Close" at the bottom: panel titles are centred so the corner is free, whereas a bottom
button has to be laid out around whatever the last row turns out to be.

Rows inside a `CreateScroll` must use `NewLayoutRow`, not `NewRow`: Jotunn's scroll content carries a
`VerticalLayoutGroup` that overwrites `anchoredPosition`, so those rows size themselves through a
`LayoutElement` and must not be passed to `LayoutColumn`.

**Do not put a Unity `Dropdown` in a Valheim scroll view.** `GUIManager.CreateDropDown` exists, but the
option list is instantiated as a child of the dropdown's own root, and the viewport's `Mask` clips it —
the popup is simply invisible. `ConfigUIPicker` parents to `CustomGUIFront` instead, and its filter box
makes it usable for lists a dropdown never could be.

`AddToggle` carries a workaround for a real Jotunn bug: `CreateToggle` parents with
`SetParent(parent)` and no `worldPositionStays: false`, which corrupts the toggle's scale. **Never call
`CreateToggle` directly** — go through `AddToggle`.

## Input blocking

Every `InputField` in a Valheim UI leaks keystrokes into the game. `CreatePanel` attaches a
`ConfigUIInputGuard` that takes a refcounted `GUIManager.BlockInput` and releases it from `OnDestroy`.
The release is tied to the component's lifecycle **on purpose**: a close-handler release does not run if
an exception is thrown mid-build or the scene changes, and the player is then stuck unable to move with
no way out but a relog.

## Localization

`AddText`, `AddButton` and the picker run their labels through `Localization.instance.Localize`, so pass
`$tokens` for anything your mod owns. Enum *member names* are deliberately left raw — they are the tokens
an admin types into a YAML file, and showing a translated form would teach them the wrong word.

The kit's own strings ("Close", "Add", "Filter…") are plain English literals, not tokens, so the folder
renders correctly when dropped into a mod that has no localization set up at all.

## Dropping this into another mod

Copy `Common/Config/UI/`, and make sure your `Logger` exposes `LogDebug` / `LogInfo` / `LogWarning` /
`LogError`.
