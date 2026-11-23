import { useState } from "react";
import { App, Button, Checkbox, Form, Input, Modal, Select, Space, Table, Tag, Typography } from "antd";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { ApiError } from "../../api/client";
import { dateTime } from "../../format";
import { useCan, useUserFormat } from "../../session";
import { AdminPermission, adminApi, type IpPool } from "./adminApi";

/** Platform sending (PROMPT 2.4, Phase 12): IP pools, warm-up of new addresses, PTR checks, TLS reporting results. */
export function SendingAdmin() {
  const { t } = useTranslation();
  const { message } = App.useApp();
  const qc = useQueryClient();
  const canEdit = useCan(AdminPermission.TenantsEdit);
  const pools = useQuery({ queryKey: ["admin-ip-pools"], queryFn: adminApi.ipPools });
  const [days, setDays] = useState(7);
  const tls = useQuery({ queryKey: ["admin-tls", days], queryFn: () => adminApi.tlsReports(days) });
  const [poolForm, setPoolForm] = useState<Partial<IpPool> | null>(null);
  const [ipForm, setIpForm] = useState<{ poolId: string } | null>(null);
  const fail = (e: unknown) => void message.error(e instanceof ApiError ? e.title : t("errors.generic"));
  const refresh = () => qc.invalidateQueries({ queryKey: ["admin-ip-pools"] });

  return (
    <Space direction="vertical" className="bm-full">
      <Space wrap>
        <Typography.Title level={5}>{t("admin.sending.pools")}</Typography.Title>
        {canEdit && <Button onClick={() => setPoolForm({ name: "", kind: "SHARED", isDefault: false })}>{t("admin.sending.addPool")}</Button>}
      </Space>
      <Typography.Paragraph type="secondary">{t("admin.sending.poolsHelp")}</Typography.Paragraph>
      {(pools.data ?? []).map((p) => (
        <section key={p.id} aria-label={p.name}>
          <Space wrap>
            <Typography.Text strong>{p.name}</Typography.Text>
            <Tag>{t(`admin.sending.kind.${p.kind}`)}</Tag>
            {p.isDefault && <Tag color="blue">{t("admin.sending.default")}</Tag>}
            <Typography.Text type="secondary">{t("admin.sending.tenants", { count: p.tenants })}</Typography.Text>
            {canEdit && <Button size="small" onClick={() => setPoolForm(p)}>{t("admin.sending.editPool")}</Button>}
            {canEdit && <Button size="small" onClick={() => setIpForm({ poolId: p.id })}>{t("admin.sending.addIp")}</Button>}
          </Space>
          <Table rowKey="id" size="small" pagination={false} dataSource={p.ips} scroll={{ x: true }}
            columns={[
              { title: t("admin.sending.address"), dataIndex: "address" },
              { title: "HELO", dataIndex: "helo" },
              { title: "PTR", render: (_, ip) => ip.ptrOk === null ? <Tag>{t("admin.sending.ptrPending")}</Tag>
                : <Tag color={ip.ptrOk ? "green" : "red"}>{t(ip.ptrOk ? "admin.sending.ptrOk" : "admin.sending.ptrBad")}</Tag> },
              { title: t("admin.sending.warmup"), render: (_, ip) => ip.capToday === null ? t("admin.sending.warm")
                : t("admin.sending.warmupDay", { day: ip.warmupDay ?? 1, cap: ip.capToday }) },
              { title: t("admin.sending.sentToday"), dataIndex: "sentToday" },
              { title: t("admin.sending.enabled"), render: (_, ip) => canEdit
                ? <Checkbox checked={ip.enabled} aria-label={t("admin.sending.enabled")}
                    onChange={(e) => void adminApi.saveSendingIp({ id: ip.id, poolId: p.id, address: ip.address, helo: ip.helo, enabled: e.target.checked, startWarmup: false })
                      .then(refresh, fail)} />
                : ip.enabled ? "✓" : "—" },
            ]} />
        </section>
      ))}

      <Space wrap>
        <Typography.Title level={5}>{t("admin.sending.tls")}</Typography.Title>
        <Select value={days} onChange={setDays} aria-label={t("admin.sending.period")}
          options={[1, 7, 30].map((d) => ({ value: d, label: t("admin.sending.lastDays", { count: d }) }))} />
      </Space>
      <Typography.Paragraph type="secondary">{t("admin.sending.tlsHelp")}</Typography.Paragraph>
      <Table rowKey={(r) => r.domain + r.policyType} size="small" loading={tls.isPending} dataSource={tls.data ?? []} scroll={{ x: true }}
        columns={[
          { title: t("admin.sending.domain"), dataIndex: "domain" },
          { title: t("admin.sending.policy"), render: (_, r) => <Tag>{r.policyType}</Tag> },
          { title: t("admin.sending.ok"), dataIndex: "successful" },
          { title: t("admin.sending.failed"), render: (_, r) => r.failed > 0 ? <Typography.Text type="danger">{r.failed}</Typography.Text> : 0 },
          { title: t("admin.sending.failureTypes"), render: (_, r) => r.failureTypes.map((f) => <Tag key={f} color="red">{f}</Tag>) },
        ]} />

      {poolForm && (
        <PoolModal pool={poolForm} onClose={() => setPoolForm(null)} onSaved={() => { setPoolForm(null); void refresh(); }} onError={fail} />
      )}
      {ipForm && (
        <IpModal poolId={ipForm.poolId} onClose={() => setIpForm(null)} onSaved={() => { setIpForm(null); void refresh(); }} onError={fail} />
      )}
    </Space>
  );
}

function PoolModal({ pool, onClose, onSaved, onError }: { pool: Partial<IpPool>; onClose: () => void; onSaved: () => void; onError: (e: unknown) => void }) {
  const { t } = useTranslation();
  const [form] = Form.useForm<{ name: string; kind: string; isDefault: boolean }>();
  return (
    <Modal open title={pool.id ? t("admin.sending.editPool") : t("admin.sending.addPool")} onCancel={onClose}
      onOk={() => void form.validateFields().then((v) => adminApi.saveIpPool({ id: pool.id ?? null, ...v })).then(onSaved, onError)}>
      <Form form={form} layout="vertical" initialValues={{ name: pool.name, kind: pool.kind ?? "SHARED", isDefault: pool.isDefault ?? false }}>
        <Form.Item name="name" label={t("admin.sending.poolName")} rules={[{ required: true, max: 100 }]}><Input /></Form.Item>
        <Form.Item name="kind" label={t("admin.sending.poolKind")}>
          <Select options={["TRIAL", "SHARED", "DEDICATED"].map((k) => ({ value: k, label: t(`admin.sending.kind.${k}`) }))} />
        </Form.Item>
        <Form.Item name="isDefault" valuePropName="checked"><Checkbox>{t("admin.sending.makeDefault")}</Checkbox></Form.Item>
      </Form>
    </Modal>
  );
}

function IpModal({ poolId, onClose, onSaved, onError }: { poolId: string; onClose: () => void; onSaved: () => void; onError: (e: unknown) => void }) {
  const { t } = useTranslation();
  const [form] = Form.useForm<{ address: string; helo: string; startWarmup: boolean }>();
  return (
    <Modal open title={t("admin.sending.addIp")} onCancel={onClose}
      onOk={() => void form.validateFields().then((v) => adminApi.saveSendingIp({ id: null, poolId, enabled: true, ...v })).then(onSaved, onError)}>
      <Form form={form} layout="vertical" initialValues={{ startWarmup: true }}>
        <Form.Item name="address" label={t("admin.sending.address")} rules={[{ required: true }]}><Input placeholder="203.0.113.10" /></Form.Item>
        <Form.Item name="helo" label={t("admin.sending.helo")} extra={t("admin.sending.heloHelp")} rules={[{ required: true }]}><Input placeholder="out1.example.com" /></Form.Item>
        <Form.Item name="startWarmup" valuePropName="checked" extra={t("admin.sending.warmupHelp")}><Checkbox>{t("admin.sending.startWarmup")}</Checkbox></Form.Item>
      </Form>
    </Modal>
  );
}

/** Users whose sending stopped because of bounces or complaints (tenant administrators lift the lock). */
export function SendingLocks() {
  const { t } = useTranslation();
  const { message } = App.useApp();
  const fmt = useUserFormat();
  const qc = useQueryClient();
  const locks = useQuery({ queryKey: ["admin-sending-locks"], queryFn: adminApi.sendingLocks });
  return (
    <>
      <Typography.Paragraph type="secondary">{t("admin.locks.help")}</Typography.Paragraph>
      <Table rowKey="userId" size="small" loading={locks.isPending} dataSource={locks.data ?? []} scroll={{ x: true }}
        locale={{ emptyText: t("admin.locks.none") }}
        columns={[
          { title: t("admin.locks.user"), render: (_, l) => <>{l.displayName} <Typography.Text type="secondary">{l.email}</Typography.Text></> },
          { title: t("admin.locks.at"), render: (_, l) => dateTime(l.lockedAt, fmt) },
          { title: t("admin.locks.reason"), render: (_, l) => t(`admin.locks.r.${(l.reason ?? "").split(":")[0]}`, { defaultValue: l.reason ?? "" }) },
          { title: t("admin.locks.stats"), render: (_, l) => t("admin.locks.statsValue", { sent: l.sent, bounces: l.hardBounces, complaints: l.complaints }) },
          { title: <span className="bm-visually-hidden">{t("common.actions")}</span>, render: (_, l) =>
            <Button size="small" onClick={() => void adminApi.unlockSending(l.userId).then(() => qc.invalidateQueries({ queryKey: ["admin-sending-locks"] }),
              (e: unknown) => void message.error(e instanceof ApiError ? e.title : t("errors.generic")))}>{t("admin.locks.unlock")}</Button> },
        ]} />
    </>
  );
}
