import type { DailyEntry } from "../../api/cluckwork";
import type { usePagedList } from "../../components/usePagedList";

export type PagedEntries = ReturnType<typeof usePagedList<DailyEntry>>;
