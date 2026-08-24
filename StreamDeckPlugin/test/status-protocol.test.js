import assert from "node:assert/strict";
import { once } from "node:events";
import { createConnection } from "node:net";
import { describe, it } from "node:test";
import {
  AppSupervisorPipeServer,
  MAXIMUM_STATUS_BYTES,
  OPEN_CONFIGURATION_COMMAND,
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

  it("hosts the pipe for an AppSupervisor client", async () => {
    const pipePath = String.raw`\\.\pipe\AppSupervisor.StreamDeck.Tests.${process.pid}.${Date.now()}`;
    let signalListening;
    const listening = new Promise((resolve) => {
      signalListening = resolve;
    });
    let signalStatus;
    const receivedStatus = new Promise((resolve) => {
      signalStatus = resolve;
    });
    const server = new AppSupervisorPipeServer({
      pipePath,
      onListening: signalListening,
      onConnectionChanged: () => {},
      onStatus: signalStatus,
    });
    server.start();

    try {
      await listening;
      const client = createConnection(pipePath);
      await once(client, "connect");

      try {
        client.write(`${validMessage()}\n`);
        const status = await receivedStatus;
        assert.equal(status.state, "supervising");

        const command = once(client, "data");
        assert.equal(server.openConfiguration(), true);
        const [data] = await command;
        assert.equal(data.toString("utf8"), OPEN_CONFIGURATION_COMMAND);
      } finally {
        client.destroy();
      }
    } finally {
      server.stop();
    }
  });
});
