import { useState } from "react";
import { App, Space, Button, Table, Tag, Typography } from "antd";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { api, ApiError } from "../../api/client";
import { Permission, type Paged, type Tenant } from "../../api/types";
import { useCan } from "../../session";
import { TenantDrawer } from "./TenantDrawer";

export function TenantsPage() {
  const { t } = useTranslation();
  const qc = useQueryClient();
  const { message } = App.useApp();
  const canEdit = useCan(Permission.PlatformTenantsEdit);
  const [page, setPage] = useState(1);
  const [open, setOpen] = useState<string | null>(null);
  const tenants = useQuery({
    queryKey: ["admin-tenants", page],
    queryFn: () => api.get<Paged<Tenant>>(`/api/v1/admin/tenants?page=${page}&pageSize=50`),
  });
  const setStatus = useMutation({
    mutationFn: (x: Tenant) => api.put<boolean>(`/api/v1/admin/tenants/${x.id}/status`, { status: x.status === "SUSPENDED" ? "ACTIVE" : "SUSPENDED" }),
    onSuccess: () => qc.invalidateQueries({ queryKey: ["admin-tenants"] }),
    onError: (e) => void message.error(e instanceof ApiError ? e.title : t("errors.generic")),
  });

  // Background jobs (Hangfire): the API sets a short-lived cookie for this browser, then the dashboard opens.
  const openJobs = async () => {
    try {
      const path = await api.post<string>("/api/v1/admin/platform/jobs-dashboard", {});
      window.open(path, "_blank", "noopener");
    } catch (e) {
      message.error(e instanceof ApiError ? e.title : t("errors.generic"));
    }
  };

  return (
    <section aria-labelledby="tenants-title">
      <Space className="bm-settings-gap" wrap style={{ justifyContent: "space-between", inlineSize: "100%" }}>
        <Typography.Title level={4} id="tenants-title" style={{ margin: 0 }}>{t("adminTenants.title")}</Typography.Title>
        <Button onClick={() => void openJobs()}>{t("adminTenants.jobs")}</Button>
      </Space>
      <Table<Tenant>
        rowKey="id"
        loading={tenants.isPending}
        dataSource={tenants.data?.items ?? []}
        scroll={{ x: true }}
        onRow={(x) => ({ onClick: () => setOpen(x.id), style: { cursor: "pointer" } })}
        pagination={{
          current: page,
          pageSize: 50,
          total: tenants.data?.total ?? 0,
          showTotal: (total, range) => t("common.rangeOfTotal", { from: range[0], to: range[1], total }),
          onChange: setPage,
        }}
        columns={[
          { title: t("adminTenants.code"), dataIndex: "code" },
          { title: t("adminTenants.name"), render: (_, x) => <span dir="auto">{x.name}</span> },
          { title: t("adminTenants.domains"), render: (_, x) => x.domains.join(", ") },
          { title: t("adminTenants.users"), dataIndex: "users" },
          { title: t("adminTenants.status"), render: (_, x) => <Tag color={x.status === "SUSPENDED" ? "warning" : "success"}>{t(`tenantStatus.${x.status}`)}</Tag> },
          {
            title: <span className="bm-visually-hidden">{t("common.actions")}</span>,
            render: (_, x) =>
              canEdit && x.kind === "CUSTOMER" ? (
                <Button size="small" danger={x.status !== "SUSPENDED"} onClick={(e) => { e.stopPropagation(); setStatus.mutate(x); }}>
                  {x.status === "SUSPENDED" ? t("adminTenants.reactivate") : t("adminTenants.suspend")}
                </Button>
              ) : null,
          },
        ]}
      />
      {open && <TenantDrawer id={open} canEdit={canEdit} onClose={() => { setOpen(null); void qc.invalidateQueries({ queryKey: ["admin-tenants"] }); }} />}
    </section>
  );
}
