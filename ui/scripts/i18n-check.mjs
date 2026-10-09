// Arabic coverage check for the dashboard.
// Fails when a string that the UI translates (tr("…") / trNode, or text props of the shared components that
// translate themselves) has no Arabic entry. Also lists raw English text left in JSX (warning, or an error
// with --strict) so coverage can be driven to zero.
import fs from "node:fs";
import path from "node:path";

const root = path.resolve(path.dirname(new URL(import.meta.url).pathname), "..");
const src = path.join(root, "src");
const strict = process.argv.includes("--strict");
const only = process.argv.find((a) => a.startsWith("--files="))?.slice(8).split(",");

function files(dir) {
  return fs.readdirSync(dir, { withFileTypes: true }).flatMap((e) =>
    e.isDirectory() ? files(path.join(dir, e.name)) : /\.(tsx?|ts)$/.test(e.name) ? [path.join(dir, e.name)] : []);
}

// Dictionary keys: i18n.ts and i18n/*.ts — `"English": "عربي"` or `Word: "عربي"`.
const dictFiles = [path.join(src, "lib/i18n.ts"), ...fs.readdirSync(path.join(src, "lib/i18n")).map((f) => path.join(src, "lib/i18n", f))];
const keys = new Set();
const unescape = (s) => s.replace(/\\(["\\'])/g, "$1");
for (const f of dictFiles) {
  const text = fs.readFileSync(f, "utf8");
  for (const m of text.matchAll(/(?:"((?:[^"\\\n]|\\.)*)"|\b([A-Za-z_][\w]*))\s*:\s*["'`]/g)) keys.add(unescape(m[1] ?? m[2]));
}

const COMPONENT_PROPS = /<(Card|Toggle|Field|PageHead|Empty|ConfirmButton|Segmented|StatusDot)\b[^>]*?\b(title|label|hint|sub|prompt)="([^"]+)"/g;
let missing = 0, raw = 0;
const report = [];
for (const f of files(src)) {
  if (f.includes(`${path.sep}lib${path.sep}i18n`)) continue;
  const rel = path.relative(root, f);
  if (only && !only.some((o) => rel.endsWith(o))) continue;
  const text = fs.readFileSync(f, "utf8");
  const lineOf = (i) => text.slice(0, i).split("\n").length;
  const need = [];
  for (const m of text.matchAll(/\btr(?:Node)?\(\s*"((?:[^"\\\n]|\\.)*)"/g)) need.push([unescape(m[1]), m.index]);
  for (const m of text.matchAll(COMPONENT_PROPS)) need.push([m[3], m.index]);
  for (const m of text.matchAll(/<Empty>([^<{]+)<\/Empty>/g)) need.push([m[1].trim(), m.index]);
  for (const [k, i] of need) if (!keys.has(k)) { missing++; report.push(`MISSING  ${rel}:${lineOf(i)}  "${k}"`); }
  if (!f.endsWith(".tsx")) continue;
  // Raw English text between tags, and literal English in DOM attributes that are shown to people.
  for (const m of text.matchAll(/>\s*([^<>{}]*?[A-Za-z]{3,}[^<>{}]*?)\s*</g)) {
    const t = m[1].trim();
    if (!t || /^[\w.-]+\(|=>|&&|\|\||^\/\//.test(t) || /^[A-Z0-9_]+$/.test(t)) continue;
    raw++; report.push(`RAW      ${rel}:${lineOf(m.index)}  ${t.slice(0, 80)}`);
  }
  for (const m of text.matchAll(/\b(placeholder|aria-label|title|alt)="([^"]*[A-Za-z]{3,}[^"]*)"/g)) {
    const before = text.slice(Math.max(0, m.index - 200), m.index);
    if (/<(Card|Toggle|Field|PageHead|Empty|ConfirmButton|Segmented|StatusDot)\b[^<]*$/.test(before)) continue; // handled above
    raw++; report.push(`RAW-ATTR ${rel}:${lineOf(m.index)}  ${m[1]}="${m[2].slice(0, 70)}"`);
  }
}
for (const line of report) console.log(line);
console.log(`\nArabic dictionary: ${keys.size} entries · missing translations: ${missing} · raw English left in JSX: ${raw}`);
if (missing > 0 || (strict && raw > 0)) process.exit(1);
