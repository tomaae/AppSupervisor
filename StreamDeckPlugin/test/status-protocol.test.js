import assert from "node:assert/strict";
import { describe, it } from "node:test";
import {
  MAXIMUM_STATUS_BYTES,
  StatusLineDecoder,
  parseStatusLine,
} from "../src/status-protocol.js";

function validMessage(overrides = {}) {
  return JSON.stringify({
    version: 1,
    state: "supervising",
    title: "Supervising",
    tooltip: "AppSupervisor - Supervising",
    image: "data:image/png;base64,AA==",
    ...overrides,
  });
}

describe("Stream Deck status protocol", () => {
  it("accepts a valid version-one status", () => {
    const status = parseStatusLine(validMessage());

    assert.equal(status.state, "supervising");
    assert.equal(status.title, "Supervising");
  });

  it("rejects malformed, unsupported, and non-PNG messages", () => {
    assert.equal(parseStatusLine("{"), undefined);
    assert.equal(parseStatusLine(validMessage({ version: 2 })), undefined);
    assert.equal(parseStatusLine(validMessage({ state: "unknown" })), undefined);
    assert.equal(
      parseStatusLine(validMessage({ image: "https://example.test/status.png" })),
      undefined,
    );
  });

  it("decodes statuses split across pipe chunks", () => {
    const decoder = new StatusLineDecoder();
    const message = validMessage();

    assert.deepEqual(decoder.push(Buffer.from(message.slice(0, 20))), []);
    const statuses = decoder.push(Buffer.from(`${message.slice(20)}\n`));

    assert.equal(statuses.length, 1);
    assert.equal(statuses[0].tooltip, "AppSupervisor - Supervising");
  });

  it("drops an oversized line and accepts the following status", () => {
    const decoder = new StatusLineDecoder();
    const oversized = "x".repeat(MAXIMUM_STATUS_BYTES + 1);
    const statuses = decoder.push(Buffer.from(`${oversized}\n${validMessage()}\n`));

    assert.equal(statuses.length, 1);
    assert.equal(statuses[0].state, "supervising");
  });
});
