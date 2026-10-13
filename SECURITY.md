# Security policy

## Supported versions

Security fixes target the latest published stable release. Update from the official [GitHub Releases](https://github.com/sebastienlevert/hotshot/releases/latest). Development builds and older releases are not a separate supported security branch.

Packages are currently unsigned. Download only official assets; package integrity checks do not replace publisher signing or establish that an unsigned installer is trustworthy.

## Report privately

Use [GitHub private vulnerability reporting](https://github.com/sebastienlevert/hotshot/security/advisories/new). Do not publish exploit details, private screenshots, credentials, account data, or a full Hotshot profile in an issue.

Describe the affected version/edition/architecture, impact, prerequisites, reproduction steps, and a minimal non-sensitive example. Keep any proof of concept bounded and avoid harming other users or systems. If private reporting is unavailable, do not place sensitive details in public comments.

Reports are reviewed on a best-effort basis; no fixed response time or bounty is promised. Coordinated disclosure is preferred.

## Boundaries worth understanding

Cloud screenshot descriptions, live coding-agent access, and capture-history access are separate opt-in choices. An enabled coding client may send returned pixels/text to its own provider. OneDrive synchronizes portable preferences, not device-local permissions, credentials, captures, or history.

Source-app exclusions also apply to screenshots extracted from recordings and to frames extracted after trimming: derivatives retain the original app, window title and monitor metadata. Explicit approval does not override a known excluded app. Images with unknown source provenance, including imports and legacy workflow labels, require per-image approval even when automatic descriptions are enabled and review-before-upload is off. The approval dialog warns that the original app exclusion cannot be verified; review and redact the saved pixels first.

GitHub sign-in is an explicit browser action using the pinned Copilot runtime's PKCE account flow. Hotshot never handles the OAuth token directly, refuses plaintext-storage consent, and opens only HTTPS GitHub authorization addresses. Sign-in uses a tool-disabled, non-persistent authentication session with no model prompt or image; pending flows are cancelled on their original connection before it is disposed. Authentication and image-upload consent remain separate. Existing environment/Copilot/GitHub CLI authentication can still be used by the SDK.

Opaque redaction covers the flattened image, but private originals and editable sidecars can retain unredacted content. Share only reviewed flattened exports. A capture folder backed up by another service may be uploaded independently of Hotshot.

The native bridge is local, same-user/same-session; there is no public HTTP listener, arbitrary file reader, shell executor, settings writer, or mouse/keyboard automation. Agent access is not a substitute for Windows account/session security.
