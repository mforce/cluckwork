// Stand-in for screens not yet built. Replaced by the real slice (#F1/#F2/#F3).
export function Placeholder({ title, issue }: { title: string; issue: string }) {
  return (
    <section>
      <h2>{title}</h2>
      <p className="muted">Not built yet — {issue}.</p>
    </section>
  );
}
