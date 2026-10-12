# Getting help with Hotshot

Start with the [documentation](https://sebastienlevert.github.io/hotshot/docs/) and its [troubleshooting guide](https://sebastienlevert.github.io/hotshot/docs/troubleshooting/). For an ordinary bug, use the repository's [bug-report form](https://github.com/sebastienlevert/hotshot/issues/new?template=bug-report.yml). For a feature, use the feature-request form and explain the problem before proposing a solution.

## Collect useful information safely

Include the app version, Core/Full/development edition, x64/ARM64 architecture, Windows build, reproduction steps, and expected/actual behavior. Mention monitor scaling, HDR, or microphone behavior only when relevant.

**Settings > About Hotshot > Show diagnostics** collects information locally. Review it before copying anything into a public issue. Remove user/organization names, private paths, window titles, account details, and unrelated capture content. Do not upload credentials, `settings.json`, a complete profile, originals/sidecars, or a whole library backup.

A safe flattened export or a generated example is better than a private screenshot. Include only the smallest excerpt needed to explain the problem.

## Try these first

| Problem | Check |
|---|---|
| Shortcut opens Snipping Tool or another app | Windows' Print Screen setting and native shortcut registration status. Choose a non-conflicting shortcut. |
| Capture/clipboard output missing | General clipboard/save choices, folder permissions, and the visible error. Videos/GIFs copy as files. |
| MCP client cannot connect | App running in the same interactive Windows session, copied executable path, native access/history choices, timed grant, and approval. Run the generated-image test. |
| Core edition cannot describe screenshots | Install the optional Copilot component explicitly or select an SDK-compatible external runtime; keep Copilot sign-in/model availability separate from component installation. |
| OneDrive preferences do not arrive | The OneDrive client, selected shared path, pending/conflict status, and unapplied Settings edits. Settings roaming does not copy captures or grant permissions. |
| Update waits | Finish work and leave Settings/editor inactive. Updates will not interrupt capture, recording, conversion, analysis, or unsaved edits. |

## Escalate the right way

Report crashes, regressions, inaccessible controls, and incorrect output as bugs. Include a reproducible generated fixture when possible. Do not delete a damaged library/settings file merely to make an error disappear; preserve it locally and report the visible recovery status.

For a suspected vulnerability, use [private vulnerability reporting](https://github.com/sebastienlevert/hotshot/security/advisories/new), not a public issue. Read [SECURITY.md](SECURITY.md) first.

Support is community/maintainer best effort, not a guaranteed response time. The newest stable release is the recommended baseline. Signing and upload/sharing services remain outside the current release scope.
