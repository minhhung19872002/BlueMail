import { useState } from "react";
import { App, Button, Checkbox, Form, Input, Modal, Space, Table, Tag, Typography } from "antd";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { api, ApiError } from "../../api/client";
import { Permission, type AdminUser, type Paged } from "../../api/types";
import { dateTime } from "../../format";
import { useCan, useMe, useUserFormat } from "../../session";

interface NewUserForm {
  email: string;
  displayName: string;
  password: string;
  isTenantAdmin: boolean;
}

export function UsersPage() {
  const { t } = useTranslation();
  const me = useMe();
  const fmt = useUserFormat();
  const qc = useQueryClient();
  const { message } = App.useApp();
  const canEdit = useCan(Permission.AdminUsersEdit);
  const [search, setSearch] = useState("");
  const [page, setPage] = useState(1);
  const [pageSize, setPageSize] = useState(50);
  const [creating, setCreating] = useState(false);
  const [form] = Form.useForm<NewUserForm>();

  const users = useQuery({
    queryKey: ["admin-users", search, page, pageSize],
    queryFn: () =>
      api.get<Paged<AdminUser>>(`/api/v1/admin/users?search=${encodeURIComponent(search)}&page=${page}&pageSize=${pageSize}`),
  });

  const onError = (e: unknown) => {
    if (e instanceof ApiError && e.fieldErrors.length > 0) {
      form.setFields(e.fieldErrors.map((f) => ({ name: f.field as keyof NewUserForm, errors: [f.message] })));
    } else {
      void message.error(e instanceof ApiError ? e.title : t("errors.generic"));
    }
  };

  const create = useMutation({
    mutationFn: (v: NewUserForm) => api.post<AdminUser>("/api/v1/admin/users", { ...v, quotaBytes: null }),
    onSuccess: async () => {
      setCreating(false);
      form.resetFields();
      void message.success(t("adminUsers.created"));
      await qc.invalidateQueries({ queryKey: ["admin-users"] });
    },
    onError,
  });

  const setStatus = useMutation({
    mutationFn: (u: AdminUser) =>
      api.put<boolean>(`/api/v1/admin/users/${u.id}/status`, { status: u.status === "ACTIVE" ? "SUSPENDED" : "ACTIVE" }),
    onSuccess: () => qc.invalidateQueries({ queryKey: ["admin-users"] }),
    onError: (e) => void message.error(e instanceof ApiError ? e.title : t("errors.generic")),
  });

  return (
    <section aria-labelledby="users-title">
      <Space style={{ inlineSize: "100%", justifyContent: "space-between" }} wrap>
        <Typography.Title level={4} id="users-title">{t("adminUsers.title")}</Typography.Title>
        <Space wrap>
          <Input.Search
            allowClear
            placeholder={t("adminUsers.search")}
            aria-label={t("adminUsers.search")}
            onSearch={(v) => {
              setPage(1);
              setSearch(v);
            }}
          />
          {canEdit && <Button type="primary" onClick={() => setCreating(true)}>{t("adminUsers.add")}</Button>}
        </Space>
      </Space>
      <Table<AdminUser>
        rowKey="id"
        loading={users.isPending}
        dataSource={users.data?.items ?? []}
        scroll={{ x: true }}
        pagination={{
          current: page,
          pageSize,
          total: users.data?.total ?? 0,
          showTotal: (total, range) => t("common.rangeOfTotal", { from: range[0], to: range[1], total }),
          onChange: (p, s) => {
            setPage(p);
            setPageSize(s);
          },
        }}
        columns={[
          { title: t("adminUsers.name"), render: (_, u) => <span dir="auto">{u.displayName}</span> },
          { title: t("adminUsers.email"), dataIndex: "email" },
          {
            title: t("adminUsers.status"),
            render: (_, u) => (
              <Space>
                <Tag color={u.status === "ACTIVE" ? "success" : "warning"}>{t(`userStatus.${u.status}`)}</Tag>
                {u.isTenantAdmin && <Tag>{t("adminUsers.admin")}</Tag>}
                {u.twoFactorEnabled && <Tag>{t("adminUsers.twoFactor")}</Tag>}
              </Space>
            ),
          },
          { title: t("adminUsers.lastLogin"), render: (_, u) => (u.lastLoginAt ? dateTime(u.lastLoginAt, fmt) : t("adminUsers.never")) },
          {
            title: <span className="bm-visually-hidden">{t("common.actions")}</span>,
            render: (_, u) =>
              canEdit && u.id !== me.id ? (
                <Button size="small" danger={u.status === "ACTIVE"} onClick={() => setStatus.mutate(u)}>
                  {u.status === "ACTIVE" ? t("adminUsers.suspend") : t("adminUsers.reactivate")}
                </Button>
              ) : null,
          },
        ]}
      />
      <Modal
        open={creating}
        title={t("adminUsers.add")}
        okText={t("common.create")}
        cancelText={t("common.cancel")}
        confirmLoading={create.isPending}
        onCancel={() => setCreating(false)}
        onOk={() => form.submit()}
        destroyOnHidden
      >
        <Form form={form} layout="vertical" onFinish={(v) => create.mutate(v)} initialValues={{ isTenantAdmin: false }}>
          <Form.Item name="email" label={t("adminUsers.email")} rules={[{ required: true, message: t("validation.required") }]}>
            <Input type="email" autoComplete="off" dir="auto" />
          </Form.Item>
          <Form.Item name="displayName" label={t("adminUsers.name")} rules={[{ required: true, message: t("validation.required") }]}>
            <Input dir="auto" />
          </Form.Item>
          <Form.Item
            name="password"
            label={t("adminUsers.initialPassword")}
            extra={t("adminUsers.mustChange")}
            rules={[{ required: true, min: 10, message: t("validation.passwordMin", { min: 10 }) }]}
          >
            <Input.Password autoComplete="new-password" />
          </Form.Item>
          <Form.Item name="isTenantAdmin" valuePropName="checked">
            <Checkbox>{t("adminUsers.makeAdmin")}</Checkbox>
          </Form.Item>
        </Form>
      </Modal>
    </section>
  );
}
