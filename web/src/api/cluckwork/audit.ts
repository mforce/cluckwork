import { apiGet } from "../client";

// --- Audit trail (#93, admin-only, read-only) ---

export interface AuditEvent {
  id: string;
  occurredAtUtc: string;
  actorEmail: string;
  action: string;
  entityType: string;
  entityId: string;
  reason: string | null;
  detailsJson: string | null;
  // #800 — the connected app the person acted through; both null for a session write.
  connectedAppClientId: string | null;
  connectedAppName: string | null;
}

export const listAuditEvents = (params?: {
  action?: string; entityType?: string; entityId?: string; from?: string; to?: string;
  connectedAppsOnly?: boolean; connectedAppClientId?: string;
  limit?: number; offset?: number;
}) => {
  const q = new URLSearchParams();
  if (params?.action) q.set("action", params.action);
  if (params?.entityType) q.set("entityType", params.entityType);
  if (params?.entityId) q.set("entityId", params.entityId);
  if (params?.from) q.set("from", params.from);
  if (params?.to) q.set("to", params.to);
  if (params?.connectedAppsOnly) q.set("connectedAppsOnly", "true");
  if (params?.connectedAppClientId) q.set("connectedAppClientId", params.connectedAppClientId);
  if (params?.limit) q.set("limit", String(params.limit));
  if (params?.offset) q.set("offset", String(params.offset));
  return apiGet<AuditEvent[]>(`/audit${q.size > 0 ? `?${q}` : ""}`);
};
