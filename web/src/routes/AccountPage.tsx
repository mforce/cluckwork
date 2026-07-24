import { useState } from "react";
import type { FormEvent } from "react";
import { changePassword, ApiError } from "../api/client";
import { useAuth } from "../auth/useAuth";

function errText(err: unknown): string {
  if (err instanceof ApiError) return err.message;
  return err instanceof Error ? err.message : String(err);
}

const MIN_LENGTH = 12;

// #165 — the one self-service surface: every role can change their own password
// by proving the current one. Changing it signs out this account's OTHER devices
// (the server revokes every refresh token) while keeping this one signed in.
export function AccountPage() {
  const { role } = useAuth();
  const [current, setCurrent] = useState("");
  const [next, setNext] = useState("");
  const [confirm, setConfirm] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [message, setMessage] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);


  async function onSubmit(e: FormEvent) {
    e.preventDefault();
    if (busy) return;
    setError(null);
    setMessage(null);

    // Caught here so a typo never costs a round-trip (the server checks too).
    if (next !== confirm) {
      setError("The new passwords don't match.");
      return;
    }
    if (next.length < MIN_LENGTH) {
      setError(`The new password must be at least ${MIN_LENGTH} characters.`);
      return;
    }

    setBusy(true);
    try {
      // No key threaded here: the server exempts this route from the response
      // cache (#165 review), since replaying it would hand back the access token
      // without the rotated refresh cookie.
      await changePassword({ currentPassword: current, newPassword: next });
      setMessage("Password changed. Any other devices have been signed out.");
      setCurrent("");
      setNext("");
      setConfirm("");
    } catch (err) {
      setError(errText(err));
    } finally {
      setBusy(false);
    }
  }

  return (
    <section>
      <div className="page-head">
        <h2>Account</h2>
      </div>
      <p className="muted">
        You are signed in with the <strong>{role}</strong> role.
      </p>

      <h3>Change password</h3>
      <p className="muted">
        Changing your password signs you out everywhere else — this device stays
        signed in.
      </p>
      <form className="inline-form" onSubmit={onSubmit}>
        <label>Current password *
          <input type="password" value={current} required autoComplete="current-password"
            onChange={(e) => setCurrent(e.target.value)} />
        </label>
        <label>New password (min {MIN_LENGTH} chars) *
          <input type="password" value={next} required minLength={MIN_LENGTH}
            autoComplete="new-password"
            onChange={(e) => setNext(e.target.value)} />
        </label>
        <label>Confirm new password *
          <input type="password" value={confirm} required autoComplete="new-password"
            onChange={(e) => setConfirm(e.target.value)} />
        </label>
        {error && <p className="error">{error}</p>}
        {message && <p className="success">{message}</p>}
        <div className="dialog-foot">
          <button type="submit" disabled={busy}>Change password</button>
        </div>
      </form>
    </section>
  );
}
