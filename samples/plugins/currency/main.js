function convert(args) {
  const from = String(args.from).toUpperCase().trim();
  const to = String(args.to).toUpperCase().trim();
  const amount = Number(args.amount);
  if (!/^[A-Z]{3}$/.test(from) || !/^[A-Z]{3}$/.test(to)) return { ok: false, message: "Use three-letter currency codes, like USD or EUR." };
  if (!isFinite(amount) || amount <= 0) return { ok: false, message: "The amount must be a positive number." };
  const r = jarvis.http.getJson("https://api.frankfurter.app/latest?amount=" + amount + "&from=" + from + "&to=" + to);
  const value = r.rates && r.rates[to];
  if (value === undefined) return { ok: false, message: "That currency isn't available." };
  return { message: amount + " " + from + " = " + value + " " + to + " (rates of " + r.date + ")", data: { amount, from, to, value, date: r.date } };
}
