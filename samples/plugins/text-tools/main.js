function word_count(args) {
  const n = String(args.text).trim().split(/\s+/).filter(w => w.length > 0).length;
  return { message: n + " words", data: { words: n } };
}
function note(args) {
  const count = Number(jarvis.storage.get("count") || "0") + 1;
  jarvis.storage.set("count", String(count));
  jarvis.storage.set("note" + count, args.text);
  return "Saved note " + count + ".";
}
