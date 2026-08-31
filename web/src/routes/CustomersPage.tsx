import { useEffect, useRef, useState } from "react";
import type { FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { Pencil, Plus } from "lucide-react";
import {
  createCustomer, formatMoney, listCustomerBalances, listCustomers, updateCustomer,
} from "../api/cluckwork";
import type { Customer, CustomerBalances } from "../api/cluckwork";
import { ApiError } from "../api/client";
import { useAuth } from "../auth/useAuth";
import { BusyButton } from "../components/BusyButton";
import { Dialog } from "../components/Dialog";
import { DialogError } from "../components/DialogError";
import { useDialogErrors } from "../components/useDialogErrors";
import { usePendingAction } from "../components/usePendingAction";
import { newId } from "../lib/ids";
import i18n from "../i18n";

function errText(err: unknown): string {
  if (err instanceof ApiError) return err.message;
  return err instanceof Error ? err.message : String(err);
}

interface EditForm {
  id: string;
  version: number;
  name: string;
  phone: string;
  email: string;
  address: string;
  note: string;
}

// #23: customer book — name + phone required, the rest optional.
export function CustomersPage() {
  const { t } = useTranslation("customers");
  const { t: tc } = useTranslation("common");

  // Balances are money data (#89): the column renders for admins only and the
  // API refuses workers regardless.
  const { isAdmin } = useAuth();
  const [customers, setCustomers] = useState<Customer[] | null>(null);
  const [balances, setBalances] = useState<CustomerBalances | null>(null);
  // #479 — one slot per PLACE a message can appear. Both reads below belong to
  // the page; the create form's failures belong to the form.
  const errors = useDialogErrors();
  const setPageError = errors.setPage;

  const [creating, setCreating] = useState(false); // F131: capture moved into a dialog
  const [name, setName] = useState("");
  const [phone, setPhone] = useState("");
  const [email, setEmail] = useState("");
  const [address, setAddress] = useState("");
  const [note, setNote] = useState("");
  const { busy, isPending, run } = usePendingAction();
  const createKey = useRef<string>(newId());

  // #625 — one edit object (id/version/all five fields) or null, following the
  // reviewed UsersPage displacement pattern: a synchronous active-target +
  // generation ref so a write that resolves after the dialog moved on (closed,
  // or reopened for a DIFFERENT customer) cannot splice its result into the
  // wrong session. Each list row already carries every field the dialog needs
  // (unlike an async detail fetch), so prefill is one atomic state assignment.
  const [editForm, setEditForm] = useState<EditForm | null>(null);
  const [editWriteInFlight, setEditWriteInFlight] = useState(false);
  const editDialogGeneration = useRef(0);
  const activeEdit = useRef<{ id: string; generation: number } | null>(null);
  // Stable idempotency key per customer, rotated only after the write is
  // CONFIRMED (before the refresh) — same contract as UsersPage's `keys`.
  const editKeys = useRef(new Map<string, string>());
  const editKeyFor = (id: string) => {
    const existing = editKeys.current.get(id);
    if (existing) return existing;
    const fresh = newId();
    editKeys.current.set(id, fresh);
    return fresh;
  };
  const clearEditKey = (id: string) => editKeys.current.delete(id);

  const load = () =>
    listCustomers().then(setCustomers)
      .catch(() => setPageError(i18n.t("customers:loadCustomersErrorMessage")));

  useEffect(() => { void load(); }, []);

  useEffect(() => {
    if (!isAdmin) return;
    // The balances read sets no `busy` and the New customer trigger is not
    // gated on it, so this can reject with the form already open. Its failure
    // is the SCREEN's — nothing about the name and phone the user is typing.
    listCustomerBalances()
      .then(setBalances)
      .catch(() => setPageError(i18n.t("customers:loadBalancesErrorMessage")));
  }, [isAdmin, setPageError]);

  const outstandingFor = (customerId: string) => {
    if (balances === null) return null;
    const row = balances.items.find((b) => b.customerId === customerId);
    // No confirmed orders → nothing owed; render an explicit zero.
    return row?.outstandingMinorUnits ?? 0;
  };

  // Dismissal empties the form's slot and mutes the attempt still out, so a
  // late failure is not reported against a session the user reopened.
  const closeCreate = () => { setCreating(false); errors.abandon("create"); };

  async function onCreate(e: FormEvent) {
    e.preventDefault();
    // The hook's ref skips a same-tick re-submit (state alone waved both
    // through); `beginAttempt` stays INSIDE run for the same reason the old
    // `setError(null)` did — a skipped run must not blank the message the
    // previous attempt left, because nothing new is going to replace it.
    await run("create", async () => {
      errors.beginAttempt("create");
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
        errors.report("create", err instanceof ApiError ? err.message : String(err));
      }
    });
  }

  // #625 — the row Edit button opens or REBINDS this dialog. A different
  // customer's row displaces the one still open: that session ends without
  // Dialog's own onClose (Escape/backdrop/close are all disabled while a
  // write is in flight, but this displacement is not one of those paths), so
  // its verdict is abandoned explicitly rather than left to render under the
  // new customer's title. Same-record re-open is not a displacement.
  function openEdit(c: Customer) {
    if (editForm !== null && editForm.id !== c.id) errors.abandon("edit-customer");
    activeEdit.current = { id: c.id, generation: ++editDialogGeneration.current };
    setEditForm({
      id: c.id, version: c.version, name: c.name, phone: c.phone,
      email: c.email ?? "", address: c.address ?? "", note: c.note ?? "",
    });
  }

  function closeEdit() {
    activeEdit.current = null;
    setEditForm(null);
    errors.abandon("edit-customer");
  }

  async function onSaveEdit(e: FormEvent) {
    e.preventDefault();
    const target = editForm;
    const dialog = activeEdit.current;
    if (!target || dialog === null || dialog.id !== target.id) return;
    const isCurrentDialog = () => activeEdit.current?.generation === dialog.generation;
    const scope = `update:${target.id}`;
    await run(scope, async () => {
      errors.beginAttempt("edit-customer");
      setEditWriteInFlight(true);
      try {
        await updateCustomer(target.id, {
          version: target.version,
          name: target.name,
          phone: target.phone,
          email: target.email || undefined,
          address: target.address || undefined,
          note: target.note || undefined,
        }, editKeyFor(target.id));
        // Clear the key the instant the WRITE is confirmed — before the
        // refresh — so a changed retry after a failed refresh cannot replay
        // this cached response (same contract as UsersPage's onUpdate/#163).
        clearEditKey(target.id);
        // Not the page-level `load()` helper: it swallows its own rejection
        // into the PAGE's error slot, which would misattribute a refresh
        // failure to the screen instead of the dialog whose write caused it.
        try {
          const fresh = await listCustomers();
          if (isCurrentDialog()) setCustomers(fresh);
        } catch (err) {
          if (isCurrentDialog()) errors.report("edit-customer", errText(err));
          return;
        }
        if (isCurrentDialog()) closeEdit();
      } catch (err) {
        if (isCurrentDialog()) errors.report("edit-customer", errText(err));
      } finally {
        setEditWriteInFlight(false);
      }
    });
  }

  return (
    <section>
      <div className="page-head">
        <h2>{t("title")}</h2>
        <button type="button" onClick={() => setCreating(true)}>
          <Plus size={16} aria-hidden /> {t("newCustomerButton")}
        </button>
      </div>

      <Dialog open={creating} title={t("newCustomerButton")} onClose={closeCreate}>
        <form className="inline-form" onSubmit={onCreate}>
          <label>{t("nameFieldLabel")}
            <input value={name} required onChange={(e) => setName(e.target.value)} />
          </label>
          <label>{t("phoneFieldLabel")}
            <input value={phone} required onChange={(e) => setPhone(e.target.value)} />
          </label>
          <label>{t("emailFieldLabel")}
            <input type="email" value={email} onChange={(e) => setEmail(e.target.value)} />
          </label>
          <label>{t("addressFieldLabel")}
            <input value={address} onChange={(e) => setAddress(e.target.value)} />
          </label>
          <label>{t("noteFieldLabel")}
            <input value={note} onChange={(e) => setNote(e.target.value)} />
          </label>
          <DialogError errors={errors} scope="create" />
          <div className="dialog-foot">
            <button type="button" className="link" onClick={closeCreate}>{tc("cancel")}</button>
            <BusyButton type="submit" busy={busy}>{t("addCustomerButton")}</BusyButton>
          </div>
        </form>
      </Dialog>

      {/* #625 — closeDisabled covers the write AND its post-write refresh:
          closing/reopening the SAME record mid-flight would load data that
          predates the write, and the write's own completion is then discarded
          by the bumped generation, leaving the reopened dialog stuck stale
          until closed and reopened again post-settle (Dialog's own #609
          precedent). */}
      <Dialog
        open={editForm !== null}
        title={t("editCustomerTitle", { name: editForm?.name ?? "" })}
        onClose={closeEdit}
        closeDisabled={editWriteInFlight}
      >
        {editForm && (
          <form className="inline-form" onSubmit={onSaveEdit}>
            <label>{t("nameFieldLabel")}
              <input value={editForm.name} required
                onChange={(e) => setEditForm({ ...editForm, name: e.target.value })} />
            </label>
            <label>{t("phoneFieldLabel")}
              <input value={editForm.phone} required
                onChange={(e) => setEditForm({ ...editForm, phone: e.target.value })} />
            </label>
            <label>{t("emailFieldLabel")}
              <input type="email" value={editForm.email}
                onChange={(e) => setEditForm({ ...editForm, email: e.target.value })} />
            </label>
            <label>{t("addressFieldLabel")}
              <input value={editForm.address}
                onChange={(e) => setEditForm({ ...editForm, address: e.target.value })} />
            </label>
            <label>{t("noteFieldLabel")}
              <input value={editForm.note}
                onChange={(e) => setEditForm({ ...editForm, note: e.target.value })} />
            </label>
            <DialogError errors={errors} scope="edit-customer" />
            <div className="dialog-foot">
              <button type="button" className="link" disabled={editWriteInFlight} onClick={closeEdit}>
                {tc("cancel")}
              </button>
              <BusyButton type="submit" disabled={busy}
                busy={isPending(`update:${editForm.id}`)}>{tc("save")}</BusyButton>
            </div>
          </form>
        )}
      </Dialog>

      {/* Unconditional since #479. The `!creating` guard this replaces was the
          #474 complaint one screen over: with a single slot, dismissing a
          failed create MOVED its message out here, where it reads as a
          screen-level failure about nothing the user is looking at. The
          dialog's message now lives in a slot only the dialog renders, so
          there is nothing here to double up on or to inherit. */}
      {errors.page && <p className="error">{errors.page}</p>}

      {customers === null ? (
        <p className="muted">{tc("loading")}</p>
      ) : customers.length === 0 ? (
        <p className="muted">{t("noCustomersMessage")}</p>
      ) : (
        <table className="data">
          <thead>
            <tr>
              <th>{t("nameHeader")}</th><th>{t("phoneHeader")}</th><th>{t("emailHeader")}</th><th>{t("addressHeader")}</th><th>{t("noteHeader")}</th>
              {isAdmin && <th>{t("outstandingHeader")}</th>}
              <th></th>
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
                <td>
                  <button type="button" className="link" onClick={() => openEdit(c)}>
                    <Pencil size={14} aria-hidden /> {t("editButton")}
                  </button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </section>
  );
}
