const { chromium } = require("@playwright/test");
const assert = require("node:assert/strict"),
  fs = require("node:fs"),
  path = require("node:path");
const { createFixture } = require("./fixtures/browser-server.cjs");
(async () => {
  const fixture = createFixture();
  await new Promise((r) => fixture.server.listen(0, "127.0.0.1", r));
  const url = `http://127.0.0.1:${fixture.server.address().port}/web/#/home?tab=browse`,
    output = path.resolve(__dirname, "../artifacts/browser-review");
  fs.mkdirSync(output, { recursive: true });
  const browser = await chromium.launch({ headless: true });
  const page = await browser.newPage({
    viewport: { width: 1440, height: 1050 },
  });
  const errors = [];
  page.on("pageerror", (e) => errors.push(e.message));
  try {
    await page.goto(url);
    await page.getByRole("heading", { name: "Your music, together" }).waitFor();
    await page.locator(".jb-card").first().waitFor();
    assert.equal(await page.locator(".jb-nav-button").count(), 4);
    await page.screenshot({
      path: path.join(output, "desktop-overview.png"),
      fullPage: true,
    });
    await page.getByRole("button", { name: "Library", exact: true }).click();
    await page.locator(".jb-card-open").first().click();
    await page.locator(".jb-track-table tbody tr").first().waitFor();
    assert.equal(await page.locator(".jb-track-table tbody tr").count(), 50);
    assert.equal(
      await page.locator(".jb-track-table input:checked").count(),
      0,
    );
    await page.locator(".jb-track-table input[type=checkbox]").first().check();
    await page.getByLabel("Sort by").selectOption("title");
    await page.getByText("1 selected", { exact: true }).waitFor();
    await page.getByRole("button", { name: "Next", exact: true }).click();
    await page.getByText("Page 2 of 3", { exact: false }).waitFor();
    assert.equal(
      await page.locator(".jb-track-table input:checked").count(),
      0,
    );
    await page
      .getByRole("button", { name: "Select all matching", exact: true })
      .click();
    await page
      .getByText("All matching tracks selected", { exact: true })
      .waitFor();
    await page
      .getByRole("button", { name: "Download selected", exact: true })
      .click();
    await page.getByText("Request accepted.", { exact: false }).waitFor();
    assert(
      fixture.state.calls.some(
        (c) => c.input.selectAll === true && c.input.collectionId,
      ),
    );
    await page.screenshot({
      path: path.join(output, "desktop-playlist.png"),
      fullPage: true,
    });
    await page.getByRole("button", { name: "Activity", exact: true }).click();
    await page.locator(".jb-download-table tbody tr").first().waitFor();
    assert.equal(await page.locator(".jb-download-table tbody tr").count(), 50);
    await page
      .locator(".jb-download-table input[type=checkbox]")
      .first()
      .check();
    await page.evaluate(
      () =>
        (window.savedRow = document.querySelector(
          ".jb-download-table tbody tr",
        )),
    );
    fixture.requests[0].Progress = 0.8;
    fixture.state.requestRevision++;
    await page.evaluate(() =>
      document.querySelector("#root")._jellyspotBrowser.refreshDownloads(),
    );
    assert(
      await page.evaluate(
        () =>
          window.savedRow ===
          document.querySelector(".jb-download-table tbody tr"),
      ),
    );
    assert.equal(
      await page.locator(".jb-download-table input:checked").count(),
      1,
    );
    await page
      .getByRole("button", { name: "Pause my downloads", exact: true })
      .click();
    await page
      .getByRole("button", { name: "Resume my downloads", exact: true })
      .waitFor();
    assert(fixture.state.paused);
    await page.screenshot({
      path: path.join(output, "desktop-downloads.png"),
      fullPage: true,
    });
    await page.evaluate(() => document.body.classList.add("light"));
    await page.screenshot({
      path: path.join(output, "desktop-light.png"),
      fullPage: true,
    });
    await page.evaluate(() => document.body.classList.remove("light"));
    for (const [width, height, name] of [
      [768, 1024, "tablet"],
      [390, 844, "mobile"],
    ]) {
      await page.setViewportSize({ width, height });
      await page.evaluate(() =>
        document
          .querySelector("#root")
          ._jellyspotBrowser.navigate({ view: "overview" }),
      );
      await page.locator(".jb-card").first().waitFor();
      await page.screenshot({
        path: path.join(output, name + "-overview.png"),
        fullPage: true,
      });
      const overflow = await page.evaluate(
        () => document.documentElement.scrollWidth > innerWidth + 2,
      );
      assert.equal(overflow, false, `${name} overview must fit viewport`);
      await page.evaluate(() =>
        document
          .querySelector("#root")
          ._jellyspotBrowser.navigate({
            view: "detail",
            collectionId: "collection0",
            name: "Night drive",
            back: "mine",
          }),
      );
      await page.locator(".jb-track-table tbody tr").first().waitFor();
      await page.screenshot({
        path: path.join(output, name + "-playlist.png"),
        fullPage: true,
      });
      assert.equal(
        await page.evaluate(
          () => document.documentElement.scrollWidth > innerWidth + 2,
        ),
        false,
        `${name} track list must fit viewport`,
      );
    }
    assert.deepEqual(errors, []);
    console.log(
      "Browser checks passed: four-view navigation, bounded rows, empty initial selection, persistent selection, all-matching requests, stable polling rows, pause controls, dark/light and responsive layouts.",
    );
    console.log("Screenshots:", output);
  } finally {
    await browser.close();
    await new Promise((r) => fixture.server.close(r));
  }
})().catch((e) => {
  console.error(e);
  process.exitCode = 1;
});
