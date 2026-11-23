import { useState } from "react";
import { Alert, App, Button, Drawer, Input, List, Select, Space, Table, Tag, Typography } from "antd";
import { CopyOutlined, DeleteOutlined, PlusOutlined, ReloadOutlined, SyncOutlined } from "@ant-design/icons";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { ApiError } from "../../api/client";
import { dateTime } from "../../format";
import { useCan, useUserFormat } from "../../session";
import { AdminPermission, adminApi, type DnsRecordCheck, type Domain } from "./adminApi";

const stateColor: Record<DnsRecordCheck["state"], string> = { OK: "green", MISSING: "red", WRONG: "orange", UNKNOWN: "default" };

/** Domains (PROMPT 7.2): add, publish the records shown, check DNS, aliases, catch-all, DKIM rotation. */
export function DomainsAdmin() {
  const { t } = useTranslation();
  const { message, modal } = App.useApp();
  const qc = useQueryClient();
  const canEdit = useCan(AdminPermission.DomainsEdit);
  const domains = useQuery({ queryKey: ["admin-domains"], queryFn: adminApi.domains });
  const [name, setName] = useState("");
  const [aliasOf, setAliasOf] = useState<string | null>(null);
  const [open, setOpen] = useState<string | null>(null);
  const fail = (e: unknown) => message.error(e instanceof ApiError ? e.title : t("errors.generic"));
  const refresh = () => qc.invalidateQueries({ queryKey: ["admin-domains"] });

  const add = async () => {
    try {
      const id = await adminApi.addDomain(name.trim(), aliasOf);
      setName("");
      await refresh();
      setOpen(id);
    } catch (e) {
      fail(e);
    }
  };

  const ok = (b: boolean) => <Tag color={b ? "green" : "default"}>{b ? "✓" : "—"}</Tag>;

  return (
    <>
      {canEdit && (
        <Space.Compact className="bm-full bm-settings-gap">
          <Input value={name} onChange={(e) => setName(e.target.value)} placeholder={t("admin.domains.add")} aria-label={t("admin.domains.add")} onPressEnter={() => void add()} />
          <Select allowClear placeholder={t("admin.domains.aliasOf")} aria-label={t("admin.domains.aliasOf")} value={aliasOf ?? undefined} onChange={(v?: string) => setAliasOf(v ?? null)}
            className="bm-filter-select" options={(domains.data ?? []).filter((d) => !d.aliasOf).map((d) => ({ value: d.id, label: d.domain }))} />
          <Button type="primary" icon={<PlusOutlined />} disabled={!name.trim()} onClick={() => void add()}>{t("admin.add")}</Button>
        </Space.Compact>
      )}
      <Table<Domain> rowKey="id" size="small" pagination={false} dataSource={domains.data ?? []} loading={domains.isPending}
        onRow={(d) => ({ onClick: () => setOpen(d.id), style: { cursor: "pointer" } })}
        columns={[
          { title: t("admin.domains.domain"), render: (_, d) => <Space>{d.domain}{d.isPrimary && <Tag color="blue">{t("admin.domains.primary")}</Tag>}{d.aliasOf && <Tag>{t("admin.domains.aliasTag", { of: d.aliasOf })}</Tag>}</Space> },
          { title: t("admin.domains.verified"), render: (_, d) => (d.verified ? <Tag color="green">{t("admin.domains.yes")}</Tag> : <Tag color="red">{t("admin.domains.no")}</Tag>) },
          { title: "MX", render: (_, d) => ok(d.mxOk) },
          { title: "SPF", render: (_, d) => ok(d.spfOk) },
          { title: "DKIM", render: (_, d) => ok(d.dkimOk) },
          { title: "DMARC", render: (_, d) => ok(d.dmarcOk) },
          { title: "MTA-STS", render: (_, d) => ok(d.mtaStsOk) },
        ]} />
      {open && <DomainDrawer id={open} canEdit={canEdit} onClose={() => { setOpen(null); void refresh(); }}
        onDelete={(d) => modal.confirm({ title: t("admin.domains.deleteConfirm", { domain: d.domain }), okButtonProps: { danger: true },
          onOk: async () => { try { await adminApi.deleteDomain(d.id); setOpen(null); await refresh(); } catch (e) { fail(e); } } })} />}
    </>
  );
}

function DomainDrawer({ id, canEdit, onClose, onDelete }: { id: string; canEdit: boolean; onClose: () => void; onDelete: (d: Domain) => void }) {
  const { t } = useTranslation();
  const { message } = App.useApp();
  const fmt = useUserFormat();
  const qc = useQueryClient();
  const [checking, setChecking] = useState(false);
  const data = useQuery({ queryKey: ["admin-domain", id], queryFn: () => adminApi.records(id, false) });
  const [catchAll, setCatchAll] = useState<string | null>(null);
  const fail = (e: unknown) => message.error(e instanceof ApiError ? e.title : t("errors.generic"));
  const check = async () => {
    setChecking(true);
    try {
      qc.setQueryData(["admin-domain", id], await adminApi.records(id, true));
    } catch (e) {
      fail(e);
    } finally {
      setChecking(false);
    }
  };
  const copy = (v: string) => void navigator.clipboard.writeText(v).then(() => message.success(t("admin.copied")));
  const d = data.data?.domain;

  return (
    <Drawer open width="min(980px, 100vw)" title={d?.domain} onClose={onClose} extra={canEdit && d && !d.isPrimary &&
      <Button danger icon={<DeleteOutlined />} onClick={() => onDelete(d)}>{t("admin.delete")}</Button>}>
      {d && (
        <Space direction="vertical" className="bm-full">
          {!d.verified && <Alert type="warning" showIcon message={t("admin.domains.unverified")} />}
          <Space wrap>
            <Button type="primary" icon={<ReloadOutlined />} loading={checking} onClick={() => void check()}>{t("admin.domains.check")}</Button>
            {d.lastCheckedAt && <Typography.Text type="secondary">{t("admin.domains.lastChecked", { at: dateTime(d.lastCheckedAt, fmt) })}</Typography.Text>}
          </Space>
          <Table<DnsRecordCheck> rowKey={(r) => r.kind + r.name + r.expected} size="small" pagination={false} dataSource={data.data?.records ?? []} scroll={{ x: true }}
            columns={[
              { title: t("admin.domains.record"), render: (_, r) => <Space direction="vertical" size={0}><strong>{t(`admin.domains.kind.${r.kind}`)}</strong>{!r.required && <Typography.Text type="secondary">{t(r.kind === "VERIFICATION" ? "admin.domains.verifiedDone" : "admin.domains.recommended")}</Typography.Text>}</Space> },
              { title: t("admin.domains.type"), dataIndex: "type" },
              { title: t("admin.domains.name"), render: (_, r) => <Space><code className="bm-query">{r.name}</code><Button size="small" type="text" icon={<CopyOutlined />} aria-label={t("admin.copy")} onClick={() => copy(r.name)} /></Space> },
              { title: t("admin.domains.value"), render: (_, r) => <Space><code className="bm-query">{r.expected}</code><Button size="small" type="text" icon={<CopyOutlined />} aria-label={t("admin.copy")} onClick={() => copy(r.expected)} /></Space> },
              { title: t("admin.domains.state"), render: (_, r) => <Space direction="vertical" size={0}><Tag color={stateColor[r.state]}>{t(`admin.domains.st.${r.state}`)}</Tag>
                {r.state === "WRONG" && r.found.map((f) => <Typography.Text key={f} type="secondary" className="bm-query">{t("admin.domains.found", { value: f })}</Typography.Text>)}</Space> },
            ]} />
          <Typography.Title level={5}>{t("admin.domains.kind.DKIM")}</Typography.Title>
          <List size="small" dataSource={d.dkimKeys} renderItem={(k) => (
            <List.Item>{k.selector} <Tag>{t(`admin.domains.dkim.${k.status}`)}</Tag>{k.retireAt && t("admin.domains.retireAt", { at: dateTime(k.retireAt, fmt) })}</List.Item>
          )} />
          {canEdit && <Button icon={<SyncOutlined />} onClick={() => void adminApi.rotateDkim(id).then(() => data.refetch(), fail)}>{t("admin.domains.rotate")}</Button>}
          {canEdit && (
            <>
              <Typography.Title level={5}>{t("admin.domains.catchAll")}</Typography.Title>
              <Space.Compact className="bm-full">
                <Input defaultValue={d.catchAllTarget ?? ""} placeholder={t("admin.domains.catchAllHint")} aria-label={t("admin.domains.catchAll")} onChange={(e) => setCatchAll(e.target.value)} />
                <Button onClick={() => void adminApi.catchAll(id, catchAll || null).then(() => message.success(t("settings.saved")), fail)}>{t("admin.save")}</Button>
              </Space.Compact>
            </>
          )}
        </Space>
      )}
    </Drawer>
  );
}
