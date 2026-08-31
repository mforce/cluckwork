import { useCallback, useEffect, useRef, useState } from "react";
import type { FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { Plus } from "lucide-react";
import { createCustomer, formatMoney, listCustomerBalances, listCustomers } from "../api/cluckwork";
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

// Matches the endpoint's DefaultPageSize (CustomerEndpoints.cs) so a full page
// is exactly what the server considers one.
const CUSTOMER_PAGE = 100;

// #23: customer book — name + phone required, the rest optional.
export function CustomersPage() {
  const { t } = useTranslation("customers");
  const { t: tc } = useTranslation("common");

  // Balances are money data (#89): the column renders for admins only and the
  // API refuses workers regardless.
  const { isAdmin } = useAuth();
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
  const { busy, run } = usePendingAction();
  const createKey = useRef<string>(newId());

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
