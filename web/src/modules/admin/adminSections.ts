import { useMe } from "../../session";
import { AdminPermission } from "./adminApi";

// Sections of the administration console and the permission each needs to be shown (the API checks again).
// Kept apart from the console itself so the app shell can ask "may this user administer?" without loading it.
export const adminSections = [
  { key: "users", any: [AdminPermission.UsersView] },
  { key: "domains", any: [AdminPermission.DomainsView] },
  { key: "groups", any: [AdminPermission.GroupsEdit] },
  { key: "policies", any: [AdminPermission.PolicyEdit] },
  { key: "security", any: [AdminPermission.SpamEdit, AdminPermission.QuarantineView] },
  { key: "operations", any: [AdminPermission.TraceView] },
  { key: "reports", any: [AdminPermission.ReportsView] },
  { key: "compliance", any: [AdminPermission.ComplianceView] },
  { key: "migration", any: [AdminPermission.MigrationEdit] },
  { key: "tenants", any: [AdminPermission.TenantsView] },
  { key: "sending", any: [AdminPermission.TenantsView] },
] as const;

export type AdminSectionKey = (typeof adminSections)[number]["key"];

export function useAdminSections() {
  const me = useMe();
  return adminSections.filter((s) => s.any.some((p) => me.permissions.includes(p)));
}
