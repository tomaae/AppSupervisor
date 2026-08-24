import { spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";

const pluginPath = fileURLToPath(
  new URL(
    "./com.tomaae.appsupervisor.sdPlugin/bin/plugin.js",
    import.meta.url,
  ),
);
const result = spawnSync(process.execPath, [pluginPath], {
  encoding: "utf8",
  timeout: 5_000,
  windowsHide: true,
});

if (result.error !== undefined) {
  throw result.error;
}

if (result.status !== 0) {
  throw new Error(
    [
      `Built plugin exited with code ${result.status}.`,
      result.stdout,
      result.stderr,
    ]
      .filter(Boolean)
      .join("\n"),
  );
}
