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

  close() {}
}

export function createFakeIndexedDb() {
  const databases = new Map<string, FakeDatabase>();

  return {
    open(name: string, _version: number) {
      const req = new FakeRequest<FakeDatabase>();
      queueMicrotask(() => {
        const existing = databases.get(name);
        const db = existing ?? new FakeDatabase();
        if (!existing) {
          databases.set(name, db);
          req.result = db;
          (req as unknown as { onupgradeneeded?: Listener }).onupgradeneeded?.();
        }
        req.succeed(db);
      });
      return req as unknown as IDBOpenDBRequest;
    },
  };
}
