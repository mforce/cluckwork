import { useEffect, useRef, useState } from "react";
import type { FormEvent } from "react";
import { Plus } from "lucide-react";
import { createCustomer, formatMoney, listCustomerBalances, listCustomers } from "../api/cluckwork";
import type { Customer, CustomerBalances } from "../api/cluckwork";
import { ApiError } from "../api/client";
import { useAuth } from "../auth/useAuth";
import { Dialog } from "../components/Dialog";
import { newId } from "../lib/ids";

// #23: customer book — name + phone required, the rest optional.
export function CustomersPage() {
  // Balances are money data (#89): the column renders for admins only and the
  // API refuses workers regardless.
  const { isAdmin } = useAuth();
  const [customers, setCustomers] = useState<Customer[] | null>(null);
  const [balances, setBalances] = useState<CustomerBalances | null>(null);
  const [error, setError] = useState<string | null>(null);

  const [creating, setCreating] = useState(false); // F131: capture moved into a dialog
  const [name, setName] = useState("");
  const [phone, setPhone] = useState("");
  const [email, setEmail] = useState("");
  const [address, setAddress] = useState("");
  const [note, setNote] = useState("");
  const [busy, setBusy] = useState(false);
  const createKey = useRef<string>(newId());

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
      createKey.current = newId();
      setName(""); setPhone(""); setEmail(""); setAddress(""); setNote("");
      setCreating(false);
      await load();
    } catch (err) {
      setError(err instanceof ApiError ? err.message : String(err));
    } finally {
      setBusy(false);
    }
  }

  return (
    <section>
      <div className="page-head">
        <h2>Customers</h2>
        <button type="button" onClick={() => { setError(null); setCreating(true); }}>
          <Plus size={16} aria-hidden /> New customer
        </button>
      </div>

      <Dialog open={creating} title="New customer" onClose={() => setCreating(false)}>
        <form className="inline-form" onSubmit={onCreate}>
          <label>Name *
            <input value={name} required onChange={(e) => setName(e.target.value)} />
          </label>
          <label>Phone *
            <input value={phone} required onChange={(e) => setPhone(e.target.value)} />
          </label>
          <label>Email
            <input type="email" value={email} onChange={(e) => setEmail(e.target.value)} />
          </label>
          <label>Address
            <input value={address} onChange={(e) => setAddress(e.target.value)} />
          </label>
          <label>Note
            <input value={note} onChange={(e) => setNote(e.target.value)} />
          </label>
          {error && <p className="error">{error}</p>}
          <div className="dialog-foot">
            <button type="button" className="link" onClick={() => setCreating(false)}>Cancel</button>
            <button type="submit" disabled={busy}>Add customer</button>
          </div>
        </form>
      </Dialog>

      {/* The dialog carries its own copy while it is up. */}
      {error && !creating && <p className="error">{error}</p>}

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
