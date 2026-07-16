import { useEffect, useRef, useState } from "react";
import type { FormEvent } from "react";
import { createCustomer, listCustomers } from "../api/cluckwork";
import type { Customer } from "../api/cluckwork";
import { ApiError } from "../api/client";

// #23: customer book — name + phone required, the rest optional.
export function CustomersPage() {
  const [customers, setCustomers] = useState<Customer[] | null>(null);
  const [error, setError] = useState<string | null>(null);

  const [name, setName] = useState("");
  const [phone, setPhone] = useState("");
  const [email, setEmail] = useState("");
  const [address, setAddress] = useState("");
  const [note, setNote] = useState("");
  const [busy, setBusy] = useState(false);
  const createKey = useRef<string>(crypto.randomUUID());

  const load = () =>
    listCustomers().then(setCustomers).catch(() => setError("Could not load customers."));

  useEffect(() => { void load(); }, []);

  async function onCreate(e: FormEvent) {
    e.preventDefault();
    if (busy) return;
    setBusy(true);
    setError(null);
    try {
      await createCustomer({
        name, phone,
        email: email || undefined,
        address: address || undefined,
        note: note || undefined,
      }, createKey.current);
      createKey.current = crypto.randomUUID();
      setName(""); setPhone(""); setEmail(""); setAddress(""); setNote("");
      await load();
    } catch (err) {
      setError(err instanceof ApiError ? err.message : String(err));
    } finally {
      setBusy(false);
    }
  }

  return (
    <section>
      <h2>Customers</h2>

      <form className="inline-form" onSubmit={onCreate}>
        <input placeholder="Name *" value={name} required
          onChange={(e) => setName(e.target.value)} />
        <input placeholder="Phone *" value={phone} required
          onChange={(e) => setPhone(e.target.value)} />
        <input placeholder="Email" type="email" value={email}
          onChange={(e) => setEmail(e.target.value)} />
        <input placeholder="Address" value={address}
          onChange={(e) => setAddress(e.target.value)} />
        <input placeholder="Note" value={note}
          onChange={(e) => setNote(e.target.value)} />
        <button type="submit" disabled={busy}>Add customer</button>
      </form>

      {error && <p className="error">{error}</p>}

      {customers === null ? (
        <p className="muted">Loading…</p>
      ) : customers.length === 0 ? (
        <p className="muted">No customers yet.</p>
      ) : (
        <table className="data">
          <thead>
            <tr><th>Name</th><th>Phone</th><th>Email</th><th>Address</th><th>Note</th></tr>
          </thead>
          <tbody>
            {customers.map((c) => (
              <tr key={c.id}>
                <td>{c.name}</td>
                <td>{c.phone}</td>
                <td>{c.email ?? "—"}</td>
                <td>{c.address ?? "—"}</td>
                <td>{c.note ?? "—"}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </section>
  );
}
