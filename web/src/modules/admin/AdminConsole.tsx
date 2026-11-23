import { Navigate, useNavigate, useParams } from "react-router-dom";
import { Tabs, Typography } from "antd";
import { useTranslation } from "react-i18next";
import { useAdminSections, type AdminSectionKey } from "./adminSections";
import { ComplianceAdmin, MigrationAdmin } from "./ComplianceAdmin";
import { DomainsAdmin } from "./DomainsAdmin";
import { GroupsAdmin } from "./GroupsAdmin";
import { OperationsAdmin } from "./OperationsAdmin";
import { PoliciesAdmin } from "./PoliciesAdmin";
import { ReportsAdmin } from "./ReportsAdmin";
import { SecurityAdmin } from "./SecurityAdmin";
import { SendingAdmin } from "./SendingAdmin";
import { TenantsPage } from "./TenantsPage";
import { UsersPage } from "./UsersPage";

const pages: Record<AdminSectionKey, () => JSX.Element> = {
  users: () => <UsersPage />,
  domains: () => <DomainsAdmin />,
  groups: () => <GroupsAdmin />,
  policies: () => <PoliciesAdmin />,
  security: () => <SecurityAdmin />,
  operations: () => <OperationsAdmin />,
  reports: () => <ReportsAdmin />,
  compliance: () => <ComplianceAdmin />,
  migration: () => <MigrationAdmin />,
  tenants: () => <TenantsPage />,
  sending: () => <SendingAdmin />,
};

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
        items={allowed.map((s) => ({ key: s.key, label: t(`admin.section.${s.key}`), children: pages[s.key]() }))} />
    </section>
  );
}
