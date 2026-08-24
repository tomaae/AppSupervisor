import { createServer } from "node:net";

export const PIPE_PATH = String.raw`\\.\pipe\AppSupervisor.StreamDeck.v1`;
export const PROTOCOL_VERSION = 1;
export const OPEN_CONFIGURATION_COMMAND = "openConfiguration\n";
export const MAXIMUM_STATUS_BYTES = 512 * 1024;

const validStates = new Set([
  "idle",
  "paused",
  "supervising",
  "error",
  "startingSupervising",
  "startingError",
  "stopping",
  "stoppingSupervising",
  "stoppingError",
]);

/** Parses and validates one AppSupervisor status line without retaining untrusted input. */
export function parseStatusLine(line) {
  let value;

  try {
    value = JSON.parse(line);
  } catch {
    return undefined;
  }

  if (
    value === null ||
    value.version !== PROTOCOL_VERSION ||
    !validStates.has(value.state) ||
    typeof value.title !== "string" ||
    value.title.length > 64 ||
    typeof value.tooltip !== "string" ||
    value.tooltip.length > 256 ||
    typeof value.image !== "string" ||
    !value.image.startsWith("data:image/png;base64,") ||
    value.image.length > MAXIMUM_STATUS_BYTES
  ) {
    return undefined;
  }

  return Object.freeze({
    state: value.state,
    title: value.title,
    tooltip: value.tooltip,
    image: value.image,
  });
}

/** Accumulates arbitrary pipe chunks into bounded newline-delimited status messages. */
export class StatusLineDecoder {
  #pending = Buffer.alloc(0);
  #discardUntilNewLine = false;

  push(chunk) {
    const statuses = [];
    let offset = 0;

    while (offset < chunk.length) {
      const newline = chunk.indexOf(0x0a, offset);
      const end = newline === -1 ? chunk.length : newline;
      const segment = chunk.subarray(offset, end);

      if (!this.#discardUntilNewLine && segment.length > 0) {
        const combinedLength = this.#pending.length + segment.length;
        if (combinedLength > MAXIMUM_STATUS_BYTES) {
          this.#pending = Buffer.alloc(0);
          this.#discardUntilNewLine = true;
        } else {
          this.#pending = this.#pending.length === 0
            ? Buffer.from(segment)
            : Buffer.concat([this.#pending, segment], combinedLength);
        }
      }

      if (newline === -1) {
        break;
      }

      if (!this.#discardUntilNewLine) {
        const line = this.#pending.toString("utf8").replace(/\r$/, "");
        const status = parseStatusLine(line);
        if (status !== undefined) {
          statuses.push(status);
        }
      }

      this.#pending = Buffer.alloc(0);
      this.#discardUntilNewLine = false;
      offset = newline + 1;
    }

    return statuses;
  }
}

/** Hosts one event-driven named-pipe connection shared by every status action instance. */
export class AppSupervisorPipeServer {
  #pipePath;
  #server;
  #socket;
  #reconnectTimer;
  #retryDelayMilliseconds = 250;
  #isConnected = false;
  #stopped = false;
  #status;

  constructor({ onStatus, onConnectionChanged, onListening = () => {}, pipePath = PIPE_PATH }) {
    this.onStatus = onStatus;
    this.onConnectionChanged = onConnectionChanged;
    this.onListening = onListening;
    this.#pipePath = pipePath;
  }

  get connected() {
    return this.#isConnected;
  }

  get status() {
    return this.#status;
  }

  start() {
    this.#stopped = false;
    this.listenNow();
  }

  stop() {
    this.#stopped = true;
    clearTimeout(this.#reconnectTimer);
    this.#reconnectTimer = undefined;
    this.#socket?.destroy();
    this.#socket = undefined;
    this.#server?.close();
    this.#server = undefined;
    this.#isConnected = false;
    this.#status = undefined;
  }

  listenNow() {
    if (this.#stopped || this.#server !== undefined) {
      return;
    }

    clearTimeout(this.#reconnectTimer);
    this.#reconnectTimer = undefined;
    const server = createServer((socket) => this.#accept(socket));
    this.#server = server;

    server.once("listening", () => {
      if (this.#server !== server) {
        return;
      }

      this.#retryDelayMilliseconds = 250;
      this.onListening();
    });
    server.once("error", () => {
      if (this.#server !== server) {
        return;
      }

      this.#server = undefined;
      server.close();
      this.#scheduleListen();
    });
    server.listen(this.#pipePath);
  }

  #accept(socket) {
    const decoder = new StatusLineDecoder();
    this.#socket?.destroy();
    this.#socket = socket;
    this.#isConnected = true;
    this.#status = undefined;
    this.onConnectionChanged(true);

    socket.on("data", (chunk) => {
      for (const status of decoder.push(chunk)) {
        this.#status = status;
        this.onStatus(status);
      }
    });
    socket.once("error", () => {
      socket.destroy();
    });
    socket.once("close", () => {
      if (this.#socket !== socket) {
        return;
      }

      const wasOnline = this.#isConnected || this.#status !== undefined;
      this.#socket = undefined;
      this.#isConnected = false;
      this.#status = undefined;
      if (wasOnline) {
        this.onConnectionChanged(false);
      }
    });
  }

  openConfiguration() {
    if (!this.connected) {
      return false;
    }

    this.#socket.write(OPEN_CONFIGURATION_COMMAND);
    return true;
  }

  #scheduleListen() {
    if (this.#stopped || this.#reconnectTimer !== undefined) {
      return;
    }

    const delay = this.#retryDelayMilliseconds;
    this.#retryDelayMilliseconds = Math.min(delay * 2, 60_000);
    this.#reconnectTimer = setTimeout(() => {
      this.#reconnectTimer = undefined;
      this.listenNow();
    }, delay);
    this.#reconnectTimer.unref();
  }
}
