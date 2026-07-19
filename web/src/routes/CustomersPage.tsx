import { useEffect, useRef, useState } from "react";
import type { FormEvent } from "react";
import { createCustomer, formatMoney, listCustomerBalances, listCustomers } from "../api/cluckwork";
import type { Customer, CustomerBalances } from "../api/cluckwork";
import { ApiError } from "../api/client";
import { useAuth } from "../auth/useAuth";

// #23: customer book — name + phone required, the rest optional.
export function CustomersPage() {
  // Balances are money data (#89): the column renders for admins only and the
  // API refuses workers regardless.
  const { isAdmin } = useAuth();
  const [customers, setCustomers] = useState<Customer[] | null>(null);
  const [balances, setBalances] = useState<CustomerBalances | null>(null);
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

  useEffect(() => {
    if (!isAdmin) return;
    listCustomerBalances()
      .then(setBalances)
      .catch(() => setError("Could not load customer balances."));
  }, [isAdmin]);

  const outstandingFor = (customerId: string) => {
    if (balances === null) return null;
    const row = balances.items.find((b) => b.customerId === customerId);
    // No confirmed orders → nothing owed; render an explicit zero.
    return row?.outstandingMinorUnits ?? 0;
  };

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
            <tr>
              <th>Name</th><th>Phone</th><th>Email</th><th>Address</th><th>Note</th>
              {isAdmin && <th>Outstanding</th>}
            </tr>
          </thead>
          <tbody>
            {customers.map((c) => (
              <tr key={c.id}>
                <td>{c.name}</td>
                <td>{c.phone}</td>
                <td>{c.email ?? "—"}</td>
                <td>{c.address ?? "—"}</td>
                <td>{c.note ?? "—"}</td>
                {isAdmin && (
                  <td>
                    {balances === null || outstandingFor(c.id) === null
                      ? "…"
                      : formatMoney(outstandingFor(c.id)!, balances.currencyCode, balances.currencyMinorUnit)}
                  </td>
                )}
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </section>
  );
}
