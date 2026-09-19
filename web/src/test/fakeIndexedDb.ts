// A minimal, hand-rolled stand-in for the one IndexedDB shape lib/bannerCache.ts
// actually uses (one object store, no indexes, no cursors, no key ranges) —
// jsdom implements no IndexedDB at all, and per the owner's #833 review, this
// repo adds no package for it (design doc §8's simplicity ceiling). Async via
// queueMicrotask, matching a real IDBRequest's own microtask-timing contract
// closely enough for `await`-based test code; nowhere near IndexedDB's full
// surface, and not meant to be.
type Listener = (() => void) | null;

class FakeRequest<T> {
  result: T | undefined;
  error: Error | null = null;
  onsuccess: Listener = null;
  onerror: Listener = null;

  succeed(result: T) {
    this.result = result;
    queueMicrotask(() => this.onsuccess?.());
  }

  fail(error: Error) {
    this.error = error;
    queueMicrotask(() => this.onerror?.());
  }
}

class FakeTransaction {
  oncomplete: Listener = null;
  onerror: Listener = null;
  onabort: Listener = null;
  #store: Map<string, unknown>;
  #pending = 0;

  constructor(store: Map<string, unknown>) {
    this.#store = store;
  }

  objectStore(_name: string) {
    const track = <T>(work: () => T): FakeRequest<T> => {
      const req = new FakeRequest<T>();
      this.#pending += 1;
      queueMicrotask(() => {
        req.succeed(work());
        this.#pending -= 1;
        if (this.#pending === 0) queueMicrotask(() => this.oncomplete?.());
      });
      return req;
    };
    return {
      get: (key: string) => track(() => this.#store.get(key)),
      put: (value: unknown, key: string) => track(() => { this.#store.set(key, value); }),
      delete: (key: string) => track(() => { this.#store.delete(key); }),
    };
  }
}

class FakeDatabase {
  #stores = new Map<string, Map<string, unknown>>();

  createObjectStore(name: string) {
    this.#stores.set(name, new Map());
  }

  transaction(name: string, _mode: string) {
    const store = this.#stores.get(name);
    if (!store) throw new Error(`FakeIndexedDb: no object store "${name}"`);
    return new FakeTransaction(store);
  }

  close() {
    // Nothing to release — the store map is retained by the factory below
    // until resetFakeIndexedDb() replaces it, the same lifetime a real
    // IDBDatabase's own connection would have across open() calls.
  }
}

export function createFakeIndexedDb() {
  const databases = new Map<string, FakeDatabase>();

  return {
    open(name: string, _version: number) {
      const req = new FakeRequest<FakeDatabase>();
      const existing = databases.get(name);
      queueMicrotask(() => {
        const db = existing ?? new FakeDatabase();
        if (!existing) {
          databases.set(name, db);
          // `result` is set BEFORE onupgradeneeded fires, matching a real
          // IDBOpenDBRequest: the handler creates the store through
          // `req.result`, which must already point at the (new) database.
          req.result = db;
          (req as unknown as { onupgradeneeded?: Listener }).onupgradeneeded?.();
        }
        req.succeed(db);
      });
      return req as unknown as IDBOpenDBRequest;
    },
  };
}
