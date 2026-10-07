# Oversight releases

Versioned store of every built installer, organized by channel.

```
releases/
├── manifest.json        Master index: every version, its channel, hashes, dates,
│                        latest-per-channel pointers.
├── beta/                Finished installer for each build lands here first (untested).
├── verified/            Installers promoted after you've tested them. PUBLISH FROM HERE.
└── versions/<ver>/      Full record of one build:
        ├── Oversight-Setup-<ver>.exe   the installer
        ├── Oversight.exe               the completed app exe
        ├── files.json                   every file in the build + version/size/sha256
        └── release.json                 this version's manifest entry
```

## Workflow

1. **Build** — bump `MyAppVersion` in [../installer/Oversight.iss](../installer/Oversight.iss), then:
   ```powershell
   powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1
   ```
   Produces `releases\beta\Oversight-Setup-<ver>.exe`, records the build under
   `versions\<ver>\`, and updates `manifest.json` (channel = **beta**).

2. **Test** the beta installer.

3. **Promote** when you're happy with it:
   ```powershell
   powershell -ExecutionPolicy Bypass -File installer\promote-release.ps1 -Version <ver>
   ```
   Copies the installer into `releases\verified\` (it also stays in `beta\`) and flips
   the manifest entry to **verified**.

4. **Publish** `releases\verified\Oversight-Setup-<ver>.exe` to your website.

See all recorded releases anytime:
```powershell
powershell -ExecutionPolicy Bypass -File installer\list-releases.ps1
```

## Publishing to your website

Publish the file from **`verified\`** (a tested build), e.g.
`verified\Oversight-Setup-2.1.4.exe`.

1. Upload it somewhere your web server can serve it.
2. Link to it, e.g.:
   ```html
   <a href="/downloads/Oversight-Setup-2.1.4.exe">Download Oversight</a>
   ```
3. Make sure it's served as a binary download (servers do this for `.exe`
   automatically) — don't put it behind anything that rewrites the bytes.

### About the SmartScreen warning

Because the installer isn't code-signed with a paid certificate, Windows
SmartScreen may show **"Windows protected your PC"** the first time someone runs
it. This is normal for indie software — users click **More info → Run anyway**.
Consider adding a short note saying so next to your download link.

To remove the warning entirely you'd need an Authenticode code-signing certificate
(a paid yearly cert from a CA), then sign the setup `.exe`. It's optional — the
installer works fine without it; users just see the one-time prompt.

## Notes

- The SHA-256 hashes in `manifest.json` / `files.json` let you verify a download
  wasn't corrupted or tampered with.
- Old versions are kept forever under `versions\` and in both channel folders, so you
  can always re-publish or roll back to a previous installer.
- `manifest.json` is plain JSON — a website can read it directly to show a version list
  or drive a "latest verified" download link.
