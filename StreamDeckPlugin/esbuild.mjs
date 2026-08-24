import { copyFile, mkdir, writeFile } from "node:fs/promises";
import { fileURLToPath } from "node:url";
import { build } from "esbuild";

const outputDirectory = new URL(
  "./com.tomaae.appsupervisor.sdPlugin/bin/",
  import.meta.url,
);

await mkdir(outputDirectory, { recursive: true });
await copyFile(
  fileURLToPath(new URL("../LICENSE", import.meta.url)),
  fileURLToPath(
    new URL(
      "./com.tomaae.appsupervisor.sdPlugin/LICENSE.txt",
      import.meta.url,
    ),
  ),
);
await writeFile(
  fileURLToPath(new URL("package.json", outputDirectory)),
  '{ "type": "module" }\n',
  "utf8",
);
await build({
  entryPoints: [fileURLToPath(new URL("./src/plugin.js", import.meta.url))],
  outfile: fileURLToPath(new URL("plugin.js", outputDirectory)),
  bundle: true,
  format: "esm",
  platform: "node",
  target: "node20",
  banner: {
    js: [
      'import { createRequire as __createRequire } from "node:module";',
      "const require = __createRequire(import.meta.url);",
    ].join("\n"),
  },
  legalComments: "eof",
  sourcemap: false,
});
