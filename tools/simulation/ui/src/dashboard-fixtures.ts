import { randomUUID } from "node:crypto";
import { test as base, expect } from "./fixtures";
import { apiGet, signInForToken } from "./api";
import { readmeFarmOwner } from "./cast";
import { API_PREFIX } from "./env";
import { farmToday } from "./farm";

export const test = base.extend<{ recordedHouse: string }>({
  recordedHouse: async ({ request }, use) => {
    const token = await signInForToken(readmeFarmOwner());
    const account = await apiGet<{ timeZoneId: string }>(token, "/account");
    const today = farmToday(account.timeZoneId);
    const name = `Row ${randomUUID().slice(0, 8)}`;
    const post = async (path: string, data?: object) => {
      const response = await request.post(`${API_PREFIX}${path}`, {
        headers: { authorization: `Bearer ${token}`, "Idempotency-Key": randomUUID() },
        data,
      });
      await expect(response).toBeOK();
      return response;
    };
    // The demo draft has an extra Continue link; compare against a submitted row.
    const flock: { id: string } = await (await post("/flocks", {
      name, breed: "ISA Brown", placementDate: today, initialCount: 10,
    })).json();
    try {
      const { farmId, houseId } = await apiGet<{ farmId: string; houseId: string }>(token, `/flocks/${flock.id}`);
      const entry: { id: string } = await (await post("/daily-entries", {
        farmId, houseId, flockId: flock.id, date: today,
        totalEggs: 0, crackedEggs: 0, dirtyEggs: 0, discardedEggs: 0, mortalityCount: 0,
        grades: [],
      })).json();
      await post(`/daily-entries/${entry.id}/submit`);
      try {
        await use(name);
      } finally {
        const { version } = await apiGet<{ version: number }>(token, `/daily-entries/${entry.id}`);
        await post(`/daily-entries/${entry.id}/void`, { version, reason: "Dashboard row-height test cleanup" });
      }
    } finally {
      await post(`/flocks/${flock.id}/archive`);
    }
  },
});
