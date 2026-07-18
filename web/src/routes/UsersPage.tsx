import { useEffect, useRef, useState } from "react";
import type { FormEvent } from "react";
import { createUser, listUsers } from "../api/cluckwork";
import type { User } from "../api/cluckwork";
import { ApiError } from "../api/client";

function errText(err: unknown): string {
  if (err instanceof ApiError) return err.message;
  return err instanceof Error ? err.message : String(err);
}

// #73 — minimal user management: create a worker (or another admin) and see
// who exists. The full user-administration UI belongs to the RBAC slice.
export function UsersPage() {
  const [users, setUsers] = useState<User[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [message, setMessage] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [role, setRole] = useState("Worker");

  // Stable idempotency keys per logical mutation, rotated only after the full
  // action (write + refresh) succeeds — same contract as the other screens.
  const keys = useRef(new Map<string, string>());
  const keyFor = (scope: string) => {
    const existing = keys.current.get(scope);
    if (existing) return existing;
    const fresh = crypto.randomUUID();
    keys.current.set(scope, fresh);
    return fresh;
  };
  const clearKey = (scope: string) => keys.current.delete(scope);

  useEffect(() => {
    listUsers()
      .then(setUsers)
      .catch((err) => setError(errText(err)));
  }, []);

  async function onCreate(e: FormEvent) {
    e.preventDefault();
    if (busy) return;
    setBusy(true);
    setError(null);
    setMessage(null);
    try {
      const scope = `create:${email.trim().toLowerCase()}`;
      await createUser({ email: email.trim(), password, role }, keyFor(scope));
      setUsers(await listUsers());
      clearKey(scope);
      setMessage(`${role} account created for ${email.trim()}.`);
      setEmail("");
      setPassword("");
      setRole("Worker");
    } catch (err) {
      setError(errText(err));
    } finally {
      setBusy(false);
    }
  }

  if (error && users === null) return <section><h2>Users</h2><p className="error">{error}</p></section>;
  if (users === null) return <section><h2>Users</h2><p className="muted">Loading…</p></section>;

  return (
    <section>
      <h2>Users</h2>
      <p className="muted">
        Workers can record the day's work — entries, purchases, feed and water,
        orders. Corrections, voids, catalogs, and flock lifecycle need an admin.
      </p>

      <form className="inline-form" onSubmit={onCreate}>
        <input type="email" placeholder="Email *" value={email} required maxLength={256}
          autoComplete="off"
          onChange={(e) => setEmail(e.target.value)} />
        <input type="password" placeholder="Password (min 12 chars) *" value={password}
          required minLength={12} autoComplete="new-password"
          onChange={(e) => setPassword(e.target.value)} />
        <select value={role} onChange={(e) => setRole(e.target.value)}>
          <option value="Worker">Worker</option>
          <option value="Admin">Admin</option>
        </select>
        <button type="submit" disabled={busy}>Create user</button>
      </form>

      {error && <p className="error">{error}</p>}
      {message && <p className="success">{message}</p>}

      <table className="data">
        <thead>
          <tr><th>Email</th><th>Name</th><th>Role</th></tr>
        </thead>
        <tbody>
          {users.map((u) => (
            <tr key={u.id}>
              <td>{u.email}</td>
              <td>{u.displayName ?? "—"}</td>
              <td>{u.role}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </section>
  );
}
