import { execFileSync } from "node:child_process";
import { readFileSync } from "node:fs";
import assert from "node:assert/strict";

// A focused pattern check, not a guarantee that arbitrary confidential data is absent.
const rules = [
  ["private-key", /-----BEGIN (?:RSA |EC |OPENSSH |DSA )?PRIVATE KEY-----/],
  ["private-rsa-xml", /<(D|P|Q|DP|DQ|InverseQ)>[A-Za-z0-9+/=\s]{32,}<\/\1>/],
  ["github-token", /\b(?:gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{40,})\b/],
  ["aws-access-key", /\b(?:AKIA|ASIA)[A-Z0-9]{16}\b/],
  ["credential-value", /(?:password|passwd|client_secret|api_key|access_token|secret_key)\s*[:=]\s*["'][^"'\r\n]{8,}["']/i],
  ["personal-path", /[A-Z]:\\Users\\(?!Public\\|Default\\)[^\\\s]+\\/i]
];
const forbidden = file => /(^|\/)(?:artifacts|node_modules|target|\.cache|\.aws|\.ssh|\.codex|\.gemini|\.vscode)(\/|$)|(^|\/)\.env(?:\..*)?$|\.(?:pfx|p12|pem|key|dpapi|dmp|log|bak|old|update|db|sqlite3?)$/i.test(file) && !/\.env\.(?:example|sample)$/.test(file);
const secretRules = value => rules.filter(([, pattern]) => pattern.test(value)).map(([name]) => name);
const git = args => execFileSync("git", args, { maxBuffer: 25 * 1024 * 1024 });
const args = process.argv.slice(2);
if (args.includes("--self-test")) {
  assert.ok(secretRules("ghp_" + "x".repeat(36)).includes("github-token"));
  assert.ok(secretRules("-----BEGIN " + "PRIVATE KEY-----").includes("private-key"));
  assert.ok(secretRules("<" + "D>" + "A".repeat(100) + "</D>").includes("private-rsa-xml"));
  assert.ok(forbidden("update-signing.dpapi"));
  assert.ok(forbidden("artifacts/hardware.json"));
  assert.ok(forbidden(".env.production"));
  assert.ok(forbidden("src-tauri/target/release/app.exe"));
  assert.equal(forbidden("assets/icons/blackwall-mark.png"), false);
  assert.equal(secretRules("https://github.com/example/project").length, 0);
  console.log("PASS: publication scanner self-check");
  process.exit(0);
}
const staged = args.includes("--staged");
const historyIndex = args.indexOf("--history");
const files = git(staged ? ["diff", "--cached", "--name-only", "--diff-filter=ACMR", "-z"] : ["ls-files", "--cached", "--others", "--exclude-standard", "-z"]).toString("utf8").split("\0").filter(Boolean);
const findings = [];
let checked = 0;
function inspect(file, bytes, revision) {
  checked++;
  if (forbidden(file) || /\.(?:exe|dll)$/i.test(file)) findings.push({ file, revision, rule: "private-or-generated-path" });
  const binary = /\.(?:exe|dll|png|ico|jpg|jpeg|woff2?)$/i.test(file);
  for (const encoding of binary ? ["latin1", "utf16le"] : ["utf8"]) {
    for (const rule of secretRules(bytes.toString(encoding))) findings.push({ file, revision, rule });
  }
}
for (const file of new Set(files)) inspect(file, staged ? git(["show", ":" + file]) : readFileSync(file), staged ? "index" : "worktree");
if (historyIndex >= 0) {
  const range = args[historyIndex + 1];
  if (!range || !/^[\w./-]+\.\.[\w./-]+$/.test(range)) throw new Error("Use --history base..head");
  const commits = git(["rev-list", range]).toString("utf8").trim().split("\n").filter(Boolean);
  for (const commit of commits) {
    const paths = git(["diff-tree", "--no-commit-id", "--name-only", "--diff-filter=ACMR", "-r", "-z", commit]).toString("utf8").split("\0").filter(Boolean);
    for (const file of paths) inspect(file, git(["show", commit + ":" + file]), commit.slice(0, 12));
  }
}
console.log(JSON.stringify({ checkedVersions: checked, findings }, null, 2));
if (findings.length) process.exitCode = 1;
