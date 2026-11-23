import { useEffect, useState } from "react";
import { App, Avatar, Button, Dropdown, Empty, Input, List, Modal, Pagination, Select, Space, Tabs, Tag, Typography, Upload } from "antd";
import { DeleteOutlined, DownloadOutlined, EditOutlined, MailOutlined, MergeCellsOutlined, MinusCircleOutlined, PlusOutlined, TeamOutlined, UploadOutlined, UserOutlined } from "@ant-design/icons";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { ApiError } from "../../api/client";
import { useCompose } from "../compose/composeStore";
import { emptyDraft } from "../compose/composeApi";
import { contactKeys, contactsApi, type Contact, type ContactInput, type LabeledValue } from "./contactsApi";

/** Contacts (PROMPT 6.10): personal contacts and groups, "Other contacts" saved automatically, the organisation directory. */
export function ContactsPage() {
  const { t } = useTranslation();
  const [tab, setTab] = useState("mine");
  return (
    <section aria-labelledby="contacts-title">
      <Typography.Title level={4} id="contacts-title">{t("contacts.title")}</Typography.Title>
      <Tabs activeKey={tab} onChange={setTab} items={[
        { key: "mine", label: t("contacts.mine"), children: <ContactList other={false} /> },
        { key: "other", label: t("contacts.other"), children: <ContactList other /> },
        { key: "dir", label: t("contacts.directory"), children: <Directory /> },
      ]} />
    </section>
  );
}

function download(blob: Blob, name: string) {
  const url = URL.createObjectURL(blob);
  const a = document.createElement("a");
  a.href = url;
  a.download = name;
  a.click();
  URL.revokeObjectURL(url);
}

function ContactList({ other }: { other: boolean }) {
  const { t } = useTranslation();
  const { message, modal } = App.useApp();
  const qc = useQueryClient();
  const openCompose = useCompose((s) => s.open);
  const [q, setQ] = useState("");
  const [group, setGroup] = useState<string | null>(null);
  const [page, setPage] = useState(1);
  const [edit, setEdit] = useState<{ id: string | null; value: ContactInput } | null>(null);
  const list = useQuery({ queryKey: [...contactKeys.all, q, group, other, page], queryFn: () => contactsApi.list(q, group, other, page) });
  const groups = useQuery({ queryKey: contactKeys.groups, queryFn: contactsApi.groups, enabled: !other });
  const refresh = () => qc.invalidateQueries({ queryKey: contactKeys.all });
  const fail = (e: unknown) => message.error(e instanceof ApiError ? e.title : t("errors.generic"));

  const importFile = async (file: File) => {
    try {
      const r = await contactsApi.importFile(file, group);
      message.success(t("contacts.imported", { created: r.created, merged: r.merged }));
      await refresh();
    } catch (e) {
      fail(e);
    }
    return false;
  };

  const newGroup = () => {
    let name = "";
    modal.confirm({
      title: t("contacts.newGroup"),
      content: <Input autoFocus maxLength={200} aria-label={t("contacts.groupName")} onChange={(e) => { name = e.target.value; }} />,
      onOk: async () => {
        if (!name.trim()) return;
        try {
          await contactsApi.saveGroup(null, name.trim());
          await qc.invalidateQueries({ queryKey: contactKeys.groups });
        } catch (e) {
          fail(e);
        }
      },
    });
  };

  const toInput = (c: Contact): ContactInput => ({ displayName: c.displayName, emails: c.emails, phones: c.phones, org: c.org, title: c.title, notes: c.notes, groupIds: c.groupIds });

  return (
    <>
      <Space wrap className="bm-contacts-bar">
        <Input.Search allowClear placeholder={t("contacts.search")} aria-label={t("contacts.search")} onSearch={(v) => { setQ(v); setPage(1); }} />
        {!other && (
          <>
            <Select className="bm-filter-select" value={group ?? ""} onChange={(v: string) => { setGroup(v || null); setPage(1); }} aria-label={t("contacts.group")}
              options={[{ value: "", label: t("contacts.allContacts") }, ...(groups.data ?? []).map((g) => ({ value: g.id, label: `${g.name} (${g.members})` }))]} />
            <Button icon={<TeamOutlined />} onClick={newGroup}>{t("contacts.newGroup")}</Button>
            {group && (
              <Button danger icon={<DeleteOutlined />} onClick={() => modal.confirm({ title: t("contacts.deleteGroup"), okButtonProps: { danger: true },
                onOk: async () => { await contactsApi.deleteGroup(group); setGroup(null); await qc.invalidateQueries({ queryKey: contactKeys.groups }); } })}>
                {t("contacts.deleteGroup")}
              </Button>
            )}
            <Button type="primary" icon={<PlusOutlined />} onClick={() => setEdit({ id: null, value: { displayName: "", emails: [{ value: "", label: null }], phones: [], org: null, title: null, notes: null, groupIds: group ? [group] : [] } })}>
              {t("contacts.new")}
            </Button>
            <Upload accept=".vcf,.csv,text/vcard,text/csv" showUploadList={false} beforeUpload={(f) => void importFile(f) as unknown as boolean}>
              <Button icon={<UploadOutlined />}>{t("contacts.import")}</Button>
            </Upload>
            <Dropdown menu={{ items: [{ key: "vcf", label: "vCard (.vcf)" }, { key: "csv", label: "CSV (Google)" }],
              onClick: ({ key }) => void contactsApi.exportFile(key as "vcf" | "csv", group).then((b) => download(b, `contacts.${key}`), fail) }}>
              <Button icon={<DownloadOutlined />}>{t("contacts.export")}</Button>
            </Dropdown>
            <Button icon={<MergeCellsOutlined />} onClick={() => void contactsApi.mergeDuplicates().then(async (n) => { message.success(t("contacts.merged", { count: n })); await refresh(); }, fail)}>
              {t("contacts.mergeDuplicates")}
            </Button>
          </>
        )}
      </Space>
      {list.data && list.data.items.length === 0 && <Empty description={t(other ? "contacts.noneOther" : "contacts.none")} />}
      <List className="bm-settings-list" dataSource={list.data?.items ?? []} renderItem={(c) => (
        <List.Item actions={[
          ...(c.emails[0] ? [<Button key="m" type="text" icon={<MailOutlined />} aria-label={t("contacts.write", { name: c.displayName })}
            onClick={() => openCompose({ initial: { ...emptyDraft(), to: [{ name: c.displayName, address: c.emails[0]!.value }] } })} />] : []),
          <Button key="e" type="text" icon={<EditOutlined />} aria-label={other ? t("contacts.save") : t("contacts.edit")} onClick={() => setEdit({ id: c.id, value: toInput(c) })} />,
          <Button key="d" type="text" danger icon={<DeleteOutlined />} aria-label={t("contacts.delete")}
            onClick={() => modal.confirm({ title: t("contacts.deleteConfirm", { name: c.displayName }), okButtonProps: { danger: true },
              onOk: async () => { await contactsApi.remove([c.id]); await refresh(); } })} />,
        ]}>
          <List.Item.Meta
            avatar={<ContactAvatar c={c} />}
            title={c.displayName}
            description={[c.emails.map((e) => e.value).join(", "), c.phones[0]?.value, [c.title, c.org].filter(Boolean).join(", ")].filter(Boolean).join(" · ")}
          />
          {c.groupIds.map((g) => <Tag key={g}>{groups.data?.find((x) => x.id === g)?.name}</Tag>)}
        </List.Item>
      )} />
      {list.data && list.data.total > 50 && (
        <Pagination current={page} pageSize={50} total={list.data.total} onChange={setPage} showSizeChanger={false} />
      )}
      {edit && (
        <ContactEditor id={edit.id} value={edit.value} groups={groups.data ?? []} onClose={() => setEdit(null)}
          onSaved={() => { setEdit(null); void refresh(); void qc.invalidateQueries({ queryKey: contactKeys.groups }); }} />
      )}
    </>
  );
}

function ContactAvatar({ c }: { c: Contact }) {
  const [src, setSrc] = useState<string | null>(null);
  useEffect(() => {
    if (!c.hasPhoto) return;
    let url: string | null = null;
    void contactsApi.photo(c.id).then((b) => { url = URL.createObjectURL(b); setSrc(url); }, () => setSrc(null));
    return () => { if (url) URL.revokeObjectURL(url); };
  }, [c.id, c.hasPhoto]);
  return src ? <Avatar src={src} alt="" /> : <Avatar icon={<UserOutlined />} />;
}

function ValueRows({ label, values, onChange, type }: { label: string; values: LabeledValue[]; onChange: (v: LabeledValue[]) => void; type: "email" | "tel" }) {
  const { t } = useTranslation();
  return (
    <Space direction="vertical" className="bm-full">
      <Typography.Text strong>{label}</Typography.Text>
      {values.map((v, i) => (
        <Space.Compact key={i} className="bm-full">
          <Input type={type} value={v.value} aria-label={label} maxLength={320} onChange={(e) => onChange(values.map((x, j) => (j === i ? { ...x, value: e.target.value } : x)))} />
          <Select className="bm-label-select" value={v.label ?? ""} aria-label={t("contacts.kind")}
            onChange={(k: string) => onChange(values.map((x, j) => (j === i ? { ...x, label: k || null } : x)))}
            options={[{ value: "", label: "—" }, ...["work", "home", "mobile", "other"].map((k) => ({ value: k, label: t(`contacts.kinds.${k}`) }))]} />
          <Button icon={<MinusCircleOutlined />} aria-label={t("contacts.removeValue")} onClick={() => onChange(values.filter((_, j) => j !== i))} />
        </Space.Compact>
      ))}
      <Button type="dashed" icon={<PlusOutlined />} onClick={() => onChange([...values, { value: "", label: null }])}>{t("contacts.add")}</Button>
    </Space>
  );
}

function ContactEditor({ id, value, groups, onClose, onSaved }: {
  id: string | null; value: ContactInput; groups: { id: string; name: string }[]; onClose: () => void; onSaved: () => void;
}) {
  const { t } = useTranslation();
  const { message } = App.useApp();
  const [c, setC] = useState(value);
  const [photo, setPhoto] = useState<File | null>(null);
  const save = async () => {
    try {
      const clean = { ...c, emails: c.emails.filter((e) => e.value.trim()), phones: c.phones.filter((p) => p.value.trim()) };
      const saved = await contactsApi.save(id, clean);
      if (photo) await contactsApi.setPhoto(saved, photo);
      onSaved();
    } catch (e) {
      message.error(e instanceof ApiError ? e.title : t("errors.generic"));
    }
  };
  return (
    <Modal open width="min(640px, 96vw)" title={id ? t("contacts.edit") : t("contacts.new")} onCancel={onClose} onOk={() => void save()} okText={t("contacts.save")}>
      <Space direction="vertical" className="bm-full">
        <Input value={c.displayName} maxLength={300} placeholder={t("contacts.name")} aria-label={t("contacts.name")} onChange={(e) => setC({ ...c, displayName: e.target.value })} />
        <ValueRows label={t("contacts.emails")} type="email" values={c.emails} onChange={(emails) => setC({ ...c, emails })} />
        <ValueRows label={t("contacts.phones")} type="tel" values={c.phones} onChange={(phones) => setC({ ...c, phones })} />
        <Input value={c.org ?? ""} maxLength={300} placeholder={t("contacts.org")} aria-label={t("contacts.org")} onChange={(e) => setC({ ...c, org: e.target.value || null })} />
        <Input value={c.title ?? ""} maxLength={300} placeholder={t("contacts.jobTitle")} aria-label={t("contacts.jobTitle")} onChange={(e) => setC({ ...c, title: e.target.value || null })} />
        <Input.TextArea value={c.notes ?? ""} maxLength={10_000} autoSize={{ minRows: 2, maxRows: 6 }} placeholder={t("contacts.notes")} aria-label={t("contacts.notes")}
          onChange={(e) => setC({ ...c, notes: e.target.value || null })} />
        {groups.length > 0 && (
          <Select mode="multiple" className="bm-full" value={c.groupIds} onChange={(groupIds) => setC({ ...c, groupIds })} aria-label={t("contacts.group")}
            placeholder={t("contacts.group")} options={groups.map((g) => ({ value: g.id, label: g.name }))} />
        )}
        <Upload accept="image/jpeg,image/png" maxCount={1} beforeUpload={(f) => { setPhoto(f); return false; }} onRemove={() => setPhoto(null)}>
          <Button icon={<UserOutlined />}>{t("contacts.photo")}</Button>
        </Upload>
      </Space>
    </Modal>
  );
}

function Directory() {
  const { t } = useTranslation();
  const [q, setQ] = useState("");
  const openCompose = useCompose((s) => s.open);
  const dir = useQuery({ queryKey: ["directory", q], queryFn: () => contactsApi.directory(q) });
  return (
    <>
      <Input.Search allowClear placeholder={t("contacts.searchDirectory")} aria-label={t("contacts.searchDirectory")} onSearch={setQ} className="bm-contacts-bar" />
      {dir.data && dir.data.length === 0 && <Empty description={t("contacts.noneDirectory")} />}
      <List className="bm-settings-list" dataSource={dir.data ?? []} renderItem={(e) => (
        <List.Item actions={[<Button key="m" type="text" icon={<MailOutlined />} aria-label={t("contacts.write", { name: e.name })}
          onClick={() => openCompose({ initial: { ...emptyDraft(), to: [{ name: e.name, address: e.email }] } })} />]}>
          <List.Item.Meta avatar={<Avatar icon={e.kind === "USER" ? <UserOutlined /> : <TeamOutlined />} />} title={e.name}
            description={[e.email, e.orgUnit, e.kind !== "USER" ? t(`contacts.kindOf.${e.kind}`) : null].filter(Boolean).join(" · ")} />
        </List.Item>
      )} />
    </>
  );
}
