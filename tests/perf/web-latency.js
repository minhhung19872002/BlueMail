// PROMPT 8.2 latency measurement through the real HTTP API with the signed-in user's token (run in the browser
// console of the BlueMail web app, or through Chrome DevTools "evaluate script"). Returns p50/p95/max in ms.
//   inbox: first page of 50 conversations of INBOX; search: 50 results for several queries.
async (options = {}) => {
  const runs = options.runs ?? 100;
  const api = options.api ?? ""; // same origin as the web app (Caddy in production, the Vite proxy in dev)
  const key = Object.keys(sessionStorage).find((k) => k.startsWith("oidc.user:"));
  const token = JSON.parse(sessionStorage.getItem(key)).access_token;
  const get = async (path) => {
    const t0 = performance.now();
    const r = await fetch(api + path, { headers: { Authorization: "Bearer " + token, Accept: "application/json" } });
    const body = await r.json();
    if (!r.ok) throw new Error(path + " " + r.status + " " + JSON.stringify(body));
    return { ms: performance.now() - t0, data: body.data };
  };
  const stats = (xs) => {
    const s = [...xs].sort((a, b) => a - b);
    const at = (p) => s[Math.min(s.length - 1, Math.ceil(p * s.length) - 1)];
    return { n: s.length, p50: Math.round(at(0.5)), p95: Math.round(at(0.95)), max: Math.round(s[s.length - 1]) };
  };
  const labels = (await get("/api/v1/mail/labels")).data.labels;
  const inbox = labels.find((l) => l.systemKey === "INBOX").id;
  const result = {};

  const inboxTimes = [];
  let total = 0;
  for (let i = 0; i < runs; i++) {
    const r = await get(`/api/v1/mail/conversations?limit=50&labelId=${inbox}`);
    inboxTimes.push(r.ms);
    total = r.data.items.length;
  }
  result.inbox = { ...stats(inboxTimes), page: total };

  const queries = options.queries ?? ["invoice", "hợp đồng", "from:sender17@partner3.example", "subject:budget review", "tokyo meeting -berlin", "has:attachment payment",
    "after:2025/01/01 before:2025/06/30 contract", "\"project schedule\"", "nonexistentword12345", "is:starred security"];
  const searchTimes = [];
  for (let i = 0; i < runs; i++) {
    const q = queries[i % queries.length];
    searchTimes.push((await get(`/api/v1/mail/conversations?limit=50&view=all&q=${encodeURIComponent(q)}`)).ms);
  }
  result.search = stats(searchTimes);
  return result;
}
