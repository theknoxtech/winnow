# Screenshots

> `icon.png` is **generated**, not a screenshot — `build\Generate-Icon.ps1` writes it alongside
> `winnow.ico`. Don't hand-edit it; change the script instead.

Drop PNGs in this folder using the exact filenames below — the README links to them.

## Currently used

| Filename | Where it appears | What it shows |
|---|---|---|
| `main-window.png` | Top of the README | The whole window after a search that returned rows. **Crop the status bar off**, or at least its right-hand end — it shows the detected host mode as `MACHINE\user`, which you probably don't want on a public repo. |
| `quick-filters.png` | Quick Filters section | The preset strip, cropped. The point is the colour-coding by group, so include enough rows to show several colours. |

## No longer referenced

`preset-editor.png`, `preset-editor-clauses.png`, `preset-editor-test.png` and
`preset-custom.png` document the preset editor in the C# version, which is retained but not
shipped (see README § Development). The PowerShell script has no preset editor — presets are
edited in the `$script:Presets` array. These files are kept in case that version is ever picked
back up; they are not linked from the README.

## Capture notes

- **Crop tightly.** A full-screen shot of a 1150×780 window scales down badly in a README.
- **Watch for real data.** Two places leak it: the results grid and detail pane contain real event
  messages from whatever machine you capture on — usernames, hostnames, IPs, file paths, domain
  names — and anything from the Security log deserves a careful look. The status bar separately
  reports the detected host mode as `MACHINE\user`. A lab VM is the safe place to shoot these.
- **PNG, not JPEG.** UI text goes blurry under JPEG compression.
- Roughly 1000–1400 px wide is plenty; GitHub scales images down to the content column anyway.
