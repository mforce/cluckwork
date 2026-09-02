import { useCallback, useEffect, useRef, useState } from "react";
import type { FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { Link } from "react-router";
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
import { usePagedList } from "../components/usePagedList";
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

// Matches the endpoint's DefaultPageSize (CustomerEndpoints.cs) so a full page
// is exactly what the server considers one.
const CUSTOMER_PAGE = 100;

// #23: customer book — name + phone required, the rest optional.
export function CustomersPage() {
  const { t } = useTranslation("customers");
  const { t: tc } = useTranslation("common");

  // Balances are money data (#89): the column renders for admins only and the
  // API refuses workers regardless.
  const { isAdmin, role } = useAuth();
  // #512 US5 (T058, FR-045) — customer names link into URL-filtered Sales,
  // but only for a role that can actually SEE Sales (the same gate
  // Dashboard's recent-sales panel already uses) — a ReadOnly/Denied user
  // would just hit a 403, so their row stays plain text.
  const canSeeSales = role !== "ReadOnly" && role !== "Denied";
  // #511 — the customer book is server-paged and unbounded; rendering one page
  // with no pager silently hid every alphabetically later customer. The empty
  // dep array is load-bearing: this list has no filter, so `fetchPage` keeps
  // ONE identity for the life of the screen, and the hook's "a new fetchPage
  // identity IS a filter change" effect never re-fires.
  const fetchCustomers = useCallback(
    (offset: number, limit: number) => listCustomers({ limit, offset }),
    [],
  );
  const customerList = usePagedList<Customer>({
    fetchPage: fetchCustomers,
    pageSize: CUSTOMER_PAGE,
    errorText: () => i18n.t("customers:loadCustomersErrorMessage"),
  });
  const customers = customerList.rows;
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
  // Stable idempotency key per customer AND exact wire payload, rotated when
  // either the Version/fields or omitted-optionals shape changes. A confirmed
  // write still clears it immediately, before the refresh.
  const editKeys = useRef(new Map<string, { key: string; fingerprint: string }>());
  const editKeyFor = (id: string, fingerprint: string) => {
    const existing = editKeys.current.get(id);
    if (existing?.fingerprint === fingerprint) return existing.key;
    const fresh = { key: newId(), fingerprint };
    editKeys.current.set(id, fresh);
    return fresh.key;
  };
  const clearEditKey = (id: string) => editKeys.current.delete(id);
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
        await customerList.runWrite(async () => {
          await createCustomer({
            name, phone,
            email: email || undefined,
            address: address || undefined,
            note: note || undefined,
          }, createKey.current);
        });
        createKey.current = newId();
        setName(""); setPhone(""); setEmail(""); setAddress(""); setNote("");
        setCreating(false);
      } catch (err) {
        errors.report("create", err instanceof ApiError ? err.message : String(err));
      }
    });
  }

  // #625 — the row Edit button opens or REBINDS this dialog. `closeDisabled`
  // makes the entire background — every OTHER row's Edit button included —
  // `inert` for the whole write+refresh window (Dialog's own modal
  // mechanism), so a live cross-record displacement is not reachable through
  // the real UI: there is no click that can reach a different row while this
  // one is in flight. The id/generation guard below is deliberate
  // defense-in-depth against that not staying true (a future relaxation of
  // `closeDisabled`, a programmatic re-open), not a path this component
  // exercises today — do not test it as if it were a live user path (#501).
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
        const body = {
          version: target.version,
          name: target.name,
          phone: target.phone,
          email: target.email || undefined,
          address: target.address || undefined,
          note: target.note || undefined,
        };
        await customerList.runWriteWithCommittedRow(async () => {
          await updateCustomer(target.id, body, editKeyFor(target.id, JSON.stringify(body)));
          // Clear the key the instant the WRITE is confirmed — before the
          // refresh — so a changed retry after a failed refresh cannot replay
          // this cached response (same contract as UsersPage's onUpdate/#163).
          clearEditKey(target.id);
          // The server's Version is now target.version + 1 (Update always
          // bumps exactly once). Rebind the form AND the hook's loaded row to
          // that COMMITTED value before it refreshes the complete loaded
          // window: a failed refresh leaves both on the real current Version,
          // including through a close+reopen of this same row.
          // Normalized exactly like Customer.Update on the server: required
          // fields trimmed, optional fields trimmed-then-null-if-blank.
          const committedVersion = target.version + 1;
          const normalizeOptional = (v: string): string | null => {
            const trimmed = v.trim();
            return trimmed === "" ? null : trimmed;
          };
          const normalizedName = target.name.trim();
          const normalizedPhone = target.phone.trim();
          const normalizedEmail = normalizeOptional(target.email);
          const normalizedAddress = normalizeOptional(target.address);
          const normalizedNote = normalizeOptional(target.note);
          const committedFields: Customer = {
            id: target.id, version: committedVersion, name: normalizedName, phone: normalizedPhone,
            email: normalizedEmail, address: normalizedAddress, note: normalizedNote,
          };
          if (isCurrentDialog()) {
            setEditForm((prev) => (prev && prev.id === target.id ? {
              ...prev, version: committedVersion, name: normalizedName, phone: normalizedPhone,
              email: normalizedEmail ?? "", address: normalizedAddress ?? "", note: normalizedNote ?? "",
            } : prev));
          }
          return committedFields;
        });
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
            {/* #625 review round 2 — disabled while the write OR its refresh
                is in flight: without this, keystrokes made after Save land in
                editForm/state but are silently discarded (the request already
                snapshotted `target`), and the field APPEARS live while the
                edit it holds cannot go anywhere. */}
            <label>{t("nameFieldLabel")}
              <input value={editForm.name} required disabled={editWriteInFlight}
                onChange={(e) => setEditForm({ ...editForm, name: e.target.value })} />
            </label>
            <label>{t("phoneFieldLabel")}
              <input value={editForm.phone} required disabled={editWriteInFlight}
                onChange={(e) => setEditForm({ ...editForm, phone: e.target.value })} />
            </label>
            <label>{t("emailFieldLabel")}
              <input type="email" value={editForm.email} disabled={editWriteInFlight}
                onChange={(e) => setEditForm({ ...editForm, email: e.target.value })} />
            </label>
            <label>{t("addressFieldLabel")}
              <input value={editForm.address} disabled={editWriteInFlight}
                onChange={(e) => setEditForm({ ...editForm, address: e.target.value })} />
            </label>
            <label>{t("noteFieldLabel")}
              <input value={editForm.note} disabled={editWriteInFlight}
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
      {customerList.error && <p className="error">{customerList.error}</p>}

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
                <td>
                  {canSeeSales
                    ? <Link className="link" to={`/sales?customerId=${c.id}`}>{c.name}</Link>
                    : c.name}
                </td>
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

      {customerList.canLoadMore && (
        // Rendered from canLoadMore, which folds in `loading`: the control is
        // withdrawn for the duration of its own flight, so two rapid clicks
        // cannot append the same page twice.
        <button className="link" onClick={() => void customerList.loadMore()}>
          {t("loadMoreButton")}
        </button>
      )}
    </section>
  );
}
