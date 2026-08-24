import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { describe, it } from "node:test";

const manifestUrl = new URL(
  "../com.tomaae.appsupervisor.sdPlugin/manifest.json",
  import.meta.url,
);
const pluginSourceUrl = new URL("../src/plugin.js", import.meta.url);

describe("Stream Deck plugin lifecycle", () => {
  it("uses the pipe connection as the only online-state authority", async () => {
    const manifest = JSON.parse(await readFile(manifestUrl, "utf8"));
    const pluginSource = await readFile(pluginSourceUrl, "utf8");

    assert.equal(manifest.ApplicationsToMonitor, undefined);
    assert.equal(pluginSource.includes("onApplicationDidTerminate"), false);
  });
});
