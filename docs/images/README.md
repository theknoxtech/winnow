# Screenshots

Drop PNGs in this folder using the exact filenames below — the README already links to them.

Until a file exists, GitHub renders a broken-image icon where it should be. If you want to push
before capturing them all, comment out the unfilled `![...](...)` lines in the README rather than
leaving them dangling.

## What to capture

| Filename | Where it appears | What should be visible |
|---|---|---|
| `main-window.png` | Top of the README | The whole window after a search that returned rows: filter panel, Quick Filters strip, populated results grid. **Crop the status bar off**, or at least its right-hand end — it shows the detected host mode as `MACHINE\user`, which you probably do not want on a public repo. |
| `quick-filters.png` | Start of **Presets** | Just the Quick Filters strip, cropped. The point is the colour-coding by group, so include enough rows to show several different colours. |
| `preset-editor.png` | **Editing presets in the app** | The editor window overall — preset list on the left with the `built-in` / `custom` labels legible, and a selected preset's fields filled in on the right. |
| `preset-editor-clauses.png` | Step 2 | The clause area, cropped. Ideally a two-clause preset (`Resource/Memory` or `DNS Errors`) so the multi-log capability is obvious. A clause with providers filled in is better than one without. |
| `preset-editor-test.png` | Step 3 | Just after clicking **Test**, with the status line showing the match count. The status line is small — crop tightly enough that it is readable. |
| `preset-custom.png` | **Adding your own** | A custom preset. Either the editor with a custom preset selected, or a before/after of the Quick Filters strip showing the new button. Both in one image is fine if it stays legible. |

## Capture notes

- **Use a light background.** The app pins its own palette, so it looks the same everywhere — no
  need to match a theme.
- **Crop tightly.** Full-screen shots of a 1150×780 window scale down badly in a README. Everything
  except `main-window.png` should be a crop of the relevant region.
- **Watch for real data.** Two places leak it. The results grid and detail pane contain real event
  messages from whatever machine you capture on — usernames, hostnames, IPs, file paths, domain
  names — and anything from the Security log is worth a careful look. The status bar separately
  reports the detected host mode as `MACHINE\user`. A lab VM or a fresh install is the safe place
  to shoot these.
- **PNG, not JPEG.** UI text goes blurry under JPEG compression.
- Roughly 1000–1400 px wide is plenty; GitHub scales images down to the content column anyway.
