// A fresh password per call that meets the Identity policy (12+ characters,
// a lowercase and an uppercase letter, a digit and a symbol), built with no
// literal in source so no test holds a credential-shaped string (#1387).
// The first eight UUID characters become letters (digits 0–9 map to g–p) and
// appear in both cases; the UUID itself adds its digit 4 and its hyphens.
export function freshPassword(): string {
  const uuid = crypto.randomUUID();
  const word = uuid.slice(0, 8).replace(/\d/g, (d) => String.fromCharCode(103 + Number(d)));
  return `${word.toUpperCase()}${word}${uuid}`;
}
