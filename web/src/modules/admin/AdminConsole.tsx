import { Navigate, useNavigate, useParams } from "react-router-dom";
import { Tabs, Typography } from "antd";
import { useTranslation } from "react-i18next";
import { useMe } from "../../session";
import { AdminPermission } from "./adminApi";
import { DomainsAdmin } from "./DomainsAdmin";
import { GroupsAdmin } from "./GroupsAdmin";
import { OperationsAdmin } from "./OperationsAdmin";
import { PoliciesAdmin } from "./PoliciesAdmin";
import { ReportsAdmin } from "./ReportsAdmin";
import { SecurityAdmin } from "./SecurityAdmin";
import { TenantsPage } from "./TenantsPage";
import { UsersPage } from "./UsersPage";

// Sections of the administration console and the permission each needs to be shown (the API checks again).
const sections = [
  { key: "users", any: [AdminPermission.UsersView], render: () => <UsersPage /> },
  { key: "domains", any: [AdminPermission.DomainsView], render: () => <DomainsAdmin /> },
  { key: "groups", any: [AdminPermission.GroupsEdit], render: () => <GroupsAdmin /> },
  { key: "policies", any: [AdminPermission.PolicyEdit], render: () => <PoliciesAdmin /> },
  { key: "security", any: [AdminPermission.SpamEdit, AdminPermission.QuarantineView], render: () => <SecurityAdmin /> },
  { key: "operations", any: [AdminPermission.TraceView], render: () => <OperationsAdmin /> },
  { key: "reports", any: [AdminPermission.ReportsView], render: () => <ReportsAdmin /> },
  { key: "tenants", any: [AdminPermission.TenantsView], render: () => <TenantsPage /> },
] as const;

export function useAdminSections() {
  const me = useMe();
  return sections.filter((s) => s.any.some((p) => me.permissions.includes(p)));
}

/** The administration console (PROMPT 7): one page, a tab per area the signed-in admin may use. */
export function AdminConsole() {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const { section } = useParams();
  const allowed = useAdminSections();
  const active = allowed.find((s) => s.key === section) ?? allowed[0];
  if (!active) return <Navigate to="/mail/inbox" replace />;
  if (active.key !== section) return <Navigate to={`/admin/${active.key}`} replace />;

  return (
    <section aria-labelledby="admin-title" className="bm-admin">
      <Typography.Title level={4} id="admin-title" className="bm-no-print">{t("admin.title")}</Typography.Title>
      <Tabs activeKey={active.key} onChange={(k) => navigate(`/admin/${k}`)} destroyOnHidden
        items={allowed.map((s) => ({ key: s.key, label: t(`admin.section.${s.key}`), children: s.render() }))} />
    </section>
  );
}
