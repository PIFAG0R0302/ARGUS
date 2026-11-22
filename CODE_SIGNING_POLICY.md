# Code Signing Policy

Free code signing provided by [SignPath.io](https://signpath.io), certificate by [SignPath Foundation](https://signpath.org).

## Roles

- **Author / Maintainer / Approver:** `PIFAG0R0302` (GitHub repository owner).
- **Build Environment:** Official releases are built reproducibly via GitHub Actions from tagged commits on the `main` branch of `https://github.com/PIFAG0R0302/ARGUS`.

## Privacy Policy

ARGUS does **not** collect, transmit, or share any personal data, telemetry, or usage analytics.
- All hardware monitoring, cleanup, disk diagnostics, and OS migration (`CopyOS`) operations run 100% locally on the user's machine.
- The only optional network request made by ARGUS is checking for newer official releases via the GitHub Releases API (`PIFAG0R0302/ARGUS-Updates`), which can be disabled in Settings.

## Signed Artifacts

- `Argus.exe` (self-contained `.NET 10` Windows x64 executable)
- `Argus-Setup-<version>-win-x64.exe` (Inno Setup 6 Windows x64 installer)
