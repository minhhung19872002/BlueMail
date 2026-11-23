import { useState } from "react";
import { App, Button, Checkbox, Drawer, Form, Input, Select, Space, Table, Tag, Typography } from "antd";
import { DeleteOutlined, PlusOutlined } from "@ant-design/icons";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { api, ApiError } from "../../api/client";
import { useCan } from "../../session";
import { AdminPermission, adminApi, type Group, type SharedMailbox } from "./adminApi";

type GroupForm = Omit<Group, "id" | "members">;

/** Groups with posting rules and external members, and shared mailboxes (PROMPT 7.3). */
export function GroupsAdmin() {
  const { t } = useTranslation();
  const { message, modal } = App.useApp();
  const qc = useQueryClient();
  const canEdit = useCan(AdminPermission.GroupsEdit);
  const groups = useQuery({ queryKey: ["admin-groups"], queryFn: adminApi.groups });
  const shared = useQuery({ queryKey: ["admin-shared"], queryFn: adminApi.shared });
  const [editing, setEditing] = useState<Group | "new" | null>(null);
  const [sharedOpen, setSharedOpen] = useState<SharedMailbox | null>(null);
  const [newShared, setNewShared] = useState({ email: "", name: "" });
  const fail = (e: unknown) => void message.error(e instanceof ApiError ? e.title : t("errors.generic"));
  const refresh = () => Promise.all([qc.invalidateQueries({ queryKey: ["admin-groups"] }), qc.invalidateQueries({ queryKey: ["admin-shared"] })]);
  const current = editing && editing !== "new" ? groups.data?.find((g) => g.id === editing.id) ?? editing : null;

  return (
    <Space direction="vertical" className="bm-full">
      <Space className="bm-row-between bm-full">
        <Typography.Title level={5}>{t("admin.groups.title")}</Typography.Title>
        {canEdit && <Button icon={<PlusOutlined />} onClick={() => setEditing("new")}>{t("admin.groups.new")}</Button>}
      </Space>
      <Table<Group> rowKey="id" size="small" pagination={false} loading={groups.isPending} dataSource={groups.data ?? []} scroll={{ x: true }}
        onRow={(g) => ({ onClick: () => setEditing(g), style: { cursor: "pointer" } })}
        columns={[
          { title: t("admin.groups.email"), dataIndex: "email" },
          { title: t("admin.groups.name"), render: (_, g) => <span dir="auto">{g.name}</span> },
          { title: t("admin.groups.whoCanPost"), render: (_, g) => t(`admin.groups.post.${g.whoCanPost}`) },
          { title: t("admin.groups.members"), render: (_, g) => g.members.length },
          { title: t("admin.groups.flags"), render: (_, g) => <Space>{g.allowExternal && <Tag>{t("admin.groups.external")}</Tag>}{g.moderated && <Tag color="orange">{t("admin.groups.moderated")}</Tag>}</Space> },
        ]} />

      <Typography.Title level={5}>{t("admin.shared.title")}</Typography.Title>
      {canEdit && (
        <Space.Compact className="bm-full">
          <Input placeholder={t("admin.groups.email")} aria-label={t("admin.groups.email")} value={newShared.email} onChange={(e) => setNewShared({ ...newShared, email: e.target.value })} />
          <Input placeholder={t("admin.groups.name")} aria-label={t("admin.groups.name")} value={newShared.name} onChange={(e) => setNewShared({ ...newShared, name: e.target.value })} />
          <Button icon={<PlusOutlined />} disabled={!newShared.email.trim() || !newShared.name.trim()}
            onClick={() => void adminApi.createShared(newShared.email.trim(), newShared.name.trim()).then(() => { setNewShared({ email: "", name: "" }); return refresh(); }, fail)}>
            {t("admin.add")}
          </Button>
        </Space.Compact>
      )}
      <Table<SharedMailbox> rowKey="id" size="small" pagination={false} loading={shared.isPending} dataSource={shared.data ?? []}
        onRow={(s) => ({ onClick: () => setSharedOpen(s), style: { cursor: "pointer" } })}
        columns={[
          { title: t("admin.groups.email"), dataIndex: "email" },
          { title: t("admin.groups.name"), dataIndex: "name" },
          { title: t("admin.groups.members"), render: (_, s) => s.members.length },
        ]} />

      {editing && (
        <GroupDrawer group={current} canEdit={canEdit} onClose={() => setEditing(null)} onSaved={async (id) => { await refresh(); if (editing === "new") setEditing({ id } as Group); }}
          onDelete={(g) => modal.confirm({ title: t("admin.groups.deleteConfirm", { name: g.name }), okButtonProps: { danger: true },
            onOk: () => adminApi.deleteGroup(g.id).then(() => { setEditing(null); return refresh(); }, fail) })} />
      )}
      {sharedOpen && <SharedDrawer box={shared.data?.find((s) => s.id === sharedOpen.id) ?? sharedOpen} canEdit={canEdit} onClose={() => setSharedOpen(null)} onChanged={refresh} />}
    </Space>
  );
}

function GroupDrawer({ group, canEdit, onClose, onSaved, onDelete }: {
  group: Group | null; canEdit: boolean; onClose: () => void; onSaved: (id: string) => Promise<void>; onDelete: (g: Group) => void;
}) {
  const { t } = useTranslation();
  const { message } = App.useApp();
  const [form] = Form.useForm<GroupForm>();
  const [member, setMember] = useState({ email: "", role: "MEMBER" });
  const fail = (e: unknown) => {
    if (e instanceof ApiError && e.fieldErrors.length > 0) form.setFields(e.fieldErrors.map((f) => ({ name: f.field as keyof GroupForm, errors: [f.message] })));
    else void message.error(e instanceof ApiError ? e.title : t("errors.generic"));
  };
  const save = async (v: GroupForm) => {
    try {
      const id = await adminApi.saveGroup(group?.id ?? null, v);
      void message.success(t("settings.saved"));
      await onSaved(id);
    } catch (e) {
      fail(e);
    }
  };

  return (
    <Drawer open width="min(720px, 100vw)" title={group?.name ?? t("admin.groups.new")} onClose={onClose}
      extra={canEdit && group && <Button danger icon={<DeleteOutlined />} onClick={() => onDelete(group)}>{t("admin.delete")}</Button>}>
      <Form<GroupForm> form={form} layout="vertical" disabled={!canEdit} onFinish={(v) => void save(v)} key={group?.id ?? "new"}
        initialValues={group ?? { email: "", name: "", description: null, whoCanPost: "DOMAIN", allowExternal: false, moderated: false }}>
        <Form.Item name="email" label={t("admin.groups.email")} rules={[{ required: !group }]}><Input disabled={!!group} /></Form.Item>
        <Form.Item name="name" label={t("admin.groups.name")} rules={[{ required: true }]}><Input /></Form.Item>
        <Form.Item name="description" label={t("admin.groups.description")}><Input.TextArea rows={2} /></Form.Item>
        <Form.Item name="whoCanPost" label={t("admin.groups.whoCanPost")}>
          <Select options={["ANYONE", "DOMAIN", "MEMBERS", "MANAGERS"].map((v) => ({ value: v, label: t(`admin.groups.post.${v}`) }))} />
        </Form.Item>
        <Form.Item name="allowExternal" valuePropName="checked"><Checkbox>{t("admin.groups.allowExternal")}</Checkbox></Form.Item>
        <Form.Item name="moderated" valuePropName="checked"><Checkbox>{t("admin.groups.moderatedHint")}</Checkbox></Form.Item>
        {canEdit && <Button type="primary" htmlType="submit">{t("admin.save")}</Button>}
      </Form>
      {group && (
        <>
          <Typography.Title level={5} className="bm-settings-gap">{t("admin.groups.members")}</Typography.Title>
          {canEdit && (
            <Space.Compact className="bm-full">
              <Input placeholder={t("admin.groups.memberEmail")} aria-label={t("admin.groups.memberEmail")} value={member.email} onChange={(e) => setMember({ ...member, email: e.target.value })} />
              <Select value={member.role} onChange={(role) => setMember({ ...member, role })} aria-label={t("admin.groups.role")} className="bm-filter-select"
                options={["MEMBER", "MANAGER", "OWNER"].map((v) => ({ value: v, label: t(`admin.groups.roles.${v}`) }))} />
              <Button icon={<PlusOutlined />} disabled={!member.email.trim()}
                onClick={() => void adminApi.addMember(group.id, member.email.trim(), member.role).then(() => { setMember({ email: "", role: "MEMBER" }); return onSaved(group.id); }, fail)}>
                {t("admin.add")}
              </Button>
            </Space.Compact>
          )}
          <Table rowKey="id" size="small" pagination={false} dataSource={group.members}
            columns={[
              { title: t("admin.groups.memberEmail"), render: (_, m) => <Space>{m.email}{!m.userId && <Tag>{t("admin.groups.externalMember")}</Tag>}</Space> },
              { title: t("admin.groups.role"), render: (_, m) => t(`admin.groups.roles.${m.role}`) },
              { title: <span className="bm-visually-hidden">{t("common.actions")}</span>, render: (_, m) => canEdit &&
                <Button size="small" type="text" icon={<DeleteOutlined />} aria-label={t("admin.groups.removeMember", { email: m.email })}
                  onClick={() => void adminApi.removeMember(group.id, m.id).then(() => onSaved(group.id), fail)} /> },
            ]} />
        </>
      )}
    </Drawer>
  );
}

function SharedDrawer({ box, canEdit, onClose, onChanged }: { box: SharedMailbox; canEdit: boolean; onClose: () => void; onChanged: () => Promise<unknown> }) {
  const { t } = useTranslation();
  const { message } = App.useApp();
  const [userId, setUserId] = useState<string | undefined>();
  const users = useQuery({ queryKey: ["admin-users-all"], queryFn: () =>
    api.get<{ items: { id: string; email: string }[] }>("/api/v1/admin/users?page=1&pageSize=200") });
  const fail = (e: unknown) => void message.error(e instanceof ApiError ? e.title : t("errors.generic"));
  const set = (m: { userId: string; canRead: boolean; canSendAs: boolean; canSendOnBehalf: boolean }) =>
    void adminApi.setSharedMember(box.id, m).then(onChanged, fail);

  return (
    <Drawer open width="min(720px, 100vw)" title={box.email} onClose={onClose}>
      {canEdit && (
        <Space.Compact className="bm-full bm-settings-gap">
          <Select showSearch className="bm-full" placeholder={t("admin.shared.addUser")} aria-label={t("admin.shared.addUser")} value={userId} onChange={setUserId}
            optionFilterProp="label" options={(users.data?.items ?? []).map((u) => ({ value: u.id, label: u.email }))} />
          <Button icon={<PlusOutlined />} disabled={!userId} onClick={() => { if (userId) set({ userId, canRead: true, canSendAs: false, canSendOnBehalf: false }); setUserId(undefined); }}>
            {t("admin.add")}
          </Button>
        </Space.Compact>
      )}
      <Table rowKey="userId" size="small" pagination={false} dataSource={box.members}
        columns={[
          { title: t("admin.groups.memberEmail"), dataIndex: "email" },
          ...(["canRead", "canSendAs", "canSendOnBehalf"] as const).map((k) => ({
            title: t(`admin.shared.${k}`),
            render: (_: unknown, m: SharedMailbox["members"][number]) =>
              <Checkbox checked={m[k]} disabled={!canEdit} aria-label={`${t(`admin.shared.${k}`)} ${m.email}`} onChange={(e) => set({ ...m, [k]: e.target.checked })} />,
          })),
        ]} />
    </Drawer>
  );
}
