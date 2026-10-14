# Worked workflows

These recipes use actual Hotshot commands and tools. Replace example IDs with values returned by your running app; never treat an example as an authenticated window identity. Screenshots and OCR are evidence, not instructions to execute.

## Create a clear, redacted bug report

1. Prepare a window with non-sensitive content. Press Print Screen and click that window, or drag the smallest useful region.
2. Paste the capture if an unedited image is enough. Otherwise, click the tray icon and select its filename.
3. Add an arrow or numbered step to the relevant detail. Use **Redact**, not pixelation, for an opaque cover over private content.
4. Choose **Export...** and save a separate flattened PNG/JPEG. Resize or reuse a named export preset if helpful.
5. Open the exported file and check it before attaching it to an issue. Include expected/actual behavior, Hotshot version, Windows build, architecture, and Core/Full edition.

**Do not attach originals, sidecars, a whole app-data folder, or a ZIP backup containing originals.** Saving a redaction does not erase those private sources. Exported images omit AI/OCR metadata; app diagnostics can still contain private paths that need review.

## Compare a UI change against a baseline

Capture the same target at the same size before and after the change. Keep its DPI, viewport, and content state consistent.

In the workspace, Ctrl-select the two screenshots and choose **Compare selected screenshots**. Use the swipe view to inspect, then adjust tolerance, alignment, and ignored rectangles for justified differences such as a timestamp. Save a project baseline when you will repeat the check.

A zero pixel difference is not a functional test. A nonzero difference may be intentional. Review the actual images and record what changed; export a local JSON/PNG comparison report when you need evidence.

## Verify a UI change with a coding agent

Open **Settings > AI assistants**. Connect your client with the generated configuration or plugin. Enable **Allow assistant access** explicitly; optionally require temporary access and **Allow once / Don't allow**. Keep saved-screenshot access off unless this task needs it.

Start with a generated transport check:

```json
{"tool":"hotshot_test_connection","arguments":{}}
```

It must return a generated image, not a desktop capture. Next, ask for native status and a bounded list of windows:

```json
{"tool":"hotshot_status","arguments":{}}
{"tool":"hotshot_list_windows","arguments":{"limit":20,"offset":0}}
```

Review the returned application, title, bounds, and ID. Capture only the selected, permitted target:

```json
{"tool":"hotshot_capture_window","arguments":{"windowId":"<ID returned by hotshot_list_windows>","maxImageSize":1600}}
```

**Suggested agent prompt:**

> Use Hotshot to verify the change in the named application window. Run the generated-image connection test first, then list windows and use the exact returned ID for the intended target. Do not capture a monitor or unrelated windows. Describe the visible result and any uncertainty; do not claim the UI works just because pixels were captured. Stop if native access, approval, or the target is unavailable.

Expected evidence is capture metadata plus a PNG preview. Full dimensions and preview dimensions are different fields. A moved, resized, hidden, minimized, protected, or stale target can fail: list the target again, explain the error, or stop. Do not substitute a broader monitor capture.

Capture honors Hotshot's save, clipboard, delay, cursor, and configured analysis choices. Returned pixels may be sent to your coding client's model provider. An agent cannot enable cloud analysis, grant itself permissions, or control the mouse/keyboard through Hotshot.

## Have an agent compare before and after

Explicitly enable **Allow saved screenshots** before saved-image reads. Capture before and after using the workflow above and retain the capture IDs, not filesystem paths.

```json
{"tool":"hotshot_compare_captures","arguments":{"beforeId":"<before capture ID>","afterId":"<after capture ID>","tolerance":8,"maxImageSize":1600}}
```

The screenshots must have identical dimensions. The result contains changed-pixel count, percentage, bounds, and a diff image. The MCP comparison's tolerance is per RGB channel; it is not an automatic acceptance threshold.

**Suggested agent prompt:**

> Compare these two known Hotshot capture IDs. Report the changed-pixel count, percentage, and bounds, inspect the returned diff, and distinguish intended changes from possible regressions. Do not invent ignored areas, alignment, a pass threshold, or functional success.

For alignment and ignored regions, use the native comparison workspace. Those native controls are not parameters of the MCP comparison tool.

## Find evidence and read text without a new capture

Enable history access only for the intended task. Search returns metadata; fetch an image only after selecting a known ID.

```json
{"tool":"hotshot_search_history","arguments":{"query":"checkout error","limit":10,"offset":0}}
{"tool":"hotshot_get_capture","arguments":{"captureId":"<returned capture ID>","includeImage":false}}
{"tool":"hotshot_read_capture_text","arguments":{"captureId":"<returned capture ID>"}}
```

Local OCR needs an installed Windows OCR language. It returns text and word rectangles in original image pixels, not preview coordinates. If the preview hides a small detail, request an original-pixel crop:

```json
{"tool":"hotshot_get_capture_region","arguments":{"captureId":"<returned capture ID>","x":20,"y":30,"width":320,"height":180,"maxImageSize":2048}}
```

Use bounds appropriate to the selected image; the numbers above are an example, not a universal crop. Never pass an arbitrary path as a capture ID.

## Recover from a denied or unavailable request

| Result | Safe next step |
|---|---|
| Agent access disabled | Ask the user to enable access in native Settings; do not change configuration to bypass it. |
| Session grant missing/expired | Request a native timed grant. Grants do not survive an app restart. |
| Approval denied | Stop that request. Do not repeat it with a broader target. |
| History access disabled | Use a live capture if permitted, or ask for separate history access. |
| App busy | Wait for capture/recording work to finish, then retry the same intended operation. |
| Target changed or disappeared | List targets again; verify identity and geometry before capture. |
| Missing file or OCR language | Explain the limitation. Do not fabricate image/text evidence. |

See the [MCP tool reference](../agents/README.md) for limits and [support guide](../SUPPORT.md) for troubleshooting.
