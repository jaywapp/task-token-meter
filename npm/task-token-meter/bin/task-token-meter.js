#!/usr/bin/env node
"use strict";

// Thin launcher. The platform package ships the verified binary from the GitHub Release;
// nothing is downloaded at install time.

const { spawnSync } = require("node:child_process");
const path = require("node:path");

const PLATFORM_PACKAGES = {
  "win32-x64": "@jaywapp/task-token-meter-win32-x64"
};

function resolveExecutable() {
  const key = `${process.platform}-${process.arch}`;
  const packageName = PLATFORM_PACKAGES[key];
  if (!packageName) {
    return {
      error:
        `task-token-meter supports win32-x64 only; this is ${key}. ` +
        "See https://github.com/jaywapp/task-token-meter for the supported targets."
    };
  }

  try {
    const manifest = require.resolve(`${packageName}/package.json`);
    return { executable: path.join(path.dirname(manifest), "dist", "task-token-meter.exe") };
  } catch {
    return {
      error:
        `The platform package ${packageName} is missing. ` +
        "Reinstall with optional dependencies enabled, for example `npm install task-token-meter`."
    };
  }
}

const resolved = resolveExecutable();
if (resolved.error) {
  process.stderr.write(`task-token-meter: ${resolved.error}\n`);
  process.exit(1);
}

const result = spawnSync(resolved.executable, process.argv.slice(2), { stdio: "inherit" });
if (result.error) {
  process.stderr.write(`task-token-meter: ${result.error.message}\n`);
  process.exit(1);
}
if (typeof result.status === "number") {
  process.exit(result.status);
}
process.exit(1);
