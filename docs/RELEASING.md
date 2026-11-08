# Releasing

Releases are cut from tags named `v<version>` (for example `v1.0.2`).

## macOS

1. Update the default `VERSION` in `scripts/build.sh` and `scripts/make-dmg.sh`, the version mentioned in the README
   (if any) and `CHANGELOG.md`. Commit and push to `main`.
2. Tag and push:
   ```bash
   git tag v1.0.2
   git push origin v1.0.2
   ```
3. The **macOS** workflow (`.github/workflows/macos.yml`) runs the tests, builds the universal app, creates
   `MDEditor-1.0.2.dmg` + `.sha256`, and attaches them to the GitHub Release of that tag (created automatically).
4. Bump the Homebrew cask in the tap repository (`JYe16/homebrew-tap`):
   ```bash
   ./scripts/update-tap.sh 1.0.2
   ```
   It downloads the released DMG, computes its SHA-256, rewrites `Casks/mdeditor.rb` and pushes. Users then get the new
   version with `brew upgrade --cask mdeditor`.

The DMG attached to the release is the **only** artifact the cask may point to: never rebuild it locally and re-upload,
or its checksum no longer matches the cask.

## Windows

The Windows workflow (added with the Windows app) attaches its installers and zips to the same release.

## Notes

- The app is ad-hoc signed and not notarized. The Homebrew cask clears the quarantine flag after install
  (`postflight_steps` in `Casks/mdeditor.rb`). If a paid Apple Developer account is added later: sign with a Developer ID,
  notarize and staple the DMG in the workflow, drop the `postflight_steps` block from the cask, and the cask can then be
  proposed to the official `homebrew/cask` once the project meets its notability rules.
- Homebrew's official cask repository only takes signed/notarized apps from notable projects, which is why MDEditor is
  distributed through its own tap for now.
