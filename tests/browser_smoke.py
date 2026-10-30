"""Render the actual extension in an existing Playwright Chromium installation.

This does not enable automation, change an IME setting, or test learning.
"""
from pathlib import Path
import uuid
from playwright.sync_api import sync_playwright

root = Path(__file__).resolve().parents[1]
output = root / "build" / "test-runs" / ("browser-" + uuid.uuid4().hex)
output.mkdir(parents=True)
extension = root / "extension"
with sync_playwright() as playwright:
    context = playwright.chromium.launch_persistent_context(
        str(output / "profile"), headless=True, channel="chromium",
        args=[f"--disable-extensions-except={extension}", f"--load-extension={extension}"],
        downloads_path=str(output / "downloads"), viewport={"width": 460, "height": 1000},
    )
    try:
        worker = context.service_workers[0] if context.service_workers else context.wait_for_event("serviceworker")
        extension_id = worker.url.split("/")[2]
        if extension_id != "djbbbndacfniipaiopkjbeipamnjaneo":
            raise RuntimeError("Unexpected extension ID")
        page = context.new_page()
        errors = []
        page.on("pageerror", lambda error: errors.append(str(error)))
        page.goto(f"chrome-extension://{extension_id}/popup.html")
        page.wait_for_function("document.querySelector('#state').textContent !== '確認しています'")
        if page.locator("#enabled").is_checked():
            raise RuntimeError("Automation must be disabled by default")
        if not page.get_by_role("button", name="元のIME設定へ復元").is_visible():
            raise RuntimeError("Explicit restore action missing")
        if not page.evaluate("document.documentElement.scrollWidth <= innerWidth"):
            raise RuntimeError("Popup horizontal overflow")
        page.screenshot(path=str(output / "popup.png"), full_page=True)
        if errors:
            raise RuntimeError("Extension page error")
        state = worker.evaluate("chrome.storage.local.get(null)")
        if state.get("enabled") is True:
            raise RuntimeError("Rendering must not enable automation")
        print("PASS: actual extension loaded; fixed ID; default disabled; popup rendered; no page errors")
        print("NOT RUN: private native-host registration, real IME switching, learning persistence")
        print("Artifact: " + str((output / "popup.png").relative_to(root)))
    finally:
        context.close()
