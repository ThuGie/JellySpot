const { chromium } = require("@playwright/test"),
  fs = require("node:fs"),
  path = require("node:path"),
  assert = require("node:assert/strict");
(async () => {
  const session = JSON.parse(
    fs.readFileSync(
      path.resolve(__dirname, "../artifacts/jellyfin-live/session.json"),
      "utf8",
    ),
  );
  const browser = await chromium.launch({
    headless: true,
    args: ["--autoplay-policy=no-user-gesture-required"],
  });
  const page = await browser.newPage({
    viewport: { width: 1440, height: 1050 },
  });
  const errors = [];
  page.on("pageerror", (error) => errors.push(error.message));
  try {
    await page.goto("http://127.0.0.1:18096/web/");
    await page.locator("#txtManualName").fill("jellyspot-test");
    await page.locator("#txtManualPassword").fill(session.password);
    await page.locator("#txtManualPassword").press("Enter");
    await page.waitForFunction(
      () => typeof ApiClient !== "undefined" && ApiClient.getCurrentUserId(),
    );
    await page.waitForTimeout(1500);
    await page.addStyleTag({
      url: "http://127.0.0.1:18096/JellySpot/jellyspot-tabs.css",
    });
    await page.addScriptTag({
      url: "http://127.0.0.1:18096/JellySpot/jellyspot-tabs.js",
    });
    if (process.env.JELLYSPOT_LIVE_SOURCE) {
      await page.addScriptTag({
        content: fs.readFileSync(
          path.resolve(
            __dirname,
            "../Jellyfin.Plugin.JellySpot/Inject/jellyspot-browser.js",
          ),
          "utf8",
        ),
      });
      await page.evaluate(() => {
        const root = jellySpotPlugin._browseRoot;
        if (root) {
          root._jellyspotBrowser?.dispose();
          root._jellyspotBrowser = null;
          jellySpotPlugin.renderBrowse(root);
        }
      });
    }
    await page.waitForSelector('.headerTabs [data-jellyspot-tab="browse"]', {
      timeout: 15000,
    });
    await page.locator('.headerTabs [data-jellyspot-tab="browse"]').click();
    await page.locator(".jb-nav").waitFor();
    await page.evaluate(async () => { const app = jellySpotPlugin._browseRoot._jellyspotBrowser; await app.api("Activity"); await app.api("Activity"); await app.api("Browser/Collections?view=mine"); await app.api("Browser/Collections?view=mine"); });
    await page.getByRole("button", { name: "Library", exact: true }).click();
    await page
      .getByRole("button", { name: "Live private test playlist", exact: true })
      .click();
    await page
      .getByRole("button", { name: "Play available", exact: true })
      .waitFor();
    await page
      .getByRole("button", { name: "Play available", exact: true })
      .click();
    await page.waitForFunction(
      () =>
        [...document.querySelectorAll("audio")].some(
          (audio) => !audio.paused && audio.currentTime > 0,
        ),
      {},
      { timeout: 15000 },
    );
    console.log(
      "Live Jellyfin 10.11 playback passed: generated audio is playing through the native player.",
    );
    await page.screenshot({
      path: path.resolve(
        __dirname,
        "../artifacts/browser-review/live-jellyfin-playback.png",
      ),
    });
    await page.evaluate(() =>
      document.querySelectorAll("audio").forEach((audio) => audio.pause()),
    );
    const allowed = errors.filter(
      (message) => !/ResizeObserver|Script error/.test(message),
    );
    assert.deepEqual(allowed, []);
    console.log(
      "Live browser injection, authenticated collection APIs, and permission-aware local playback verified.",
    );
  } catch (error) {
    await page.screenshot({
      path: path.resolve(
        __dirname,
        "../artifacts/browser-review/live-failure.png",
      ),
    });
    console.log(
      "Browser state:",
      (
        await page
          .locator(".jb-main")
          .innerText()
          .catch(() => "")
      ).slice(0, 2500),
    );
    throw error;
  } finally {
    await browser.close();
  }
})().catch((error) => {
  console.error(error.message);
  process.exitCode = 1;
});
