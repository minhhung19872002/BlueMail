import { useState } from "react";
import { App, Button, DatePicker, Descriptions, Drawer, Input, Segmented, Select, Space, Statistic, Table, Tag, Typography } from "antd";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import type { Dayjs } from "dayjs";
import { ApiError } from "../../api/client";
import { dateTime } from "../../format";
import { useCan, useUserFormat } from "../../session";
import { AdminPermission, adminApi, type QueueItem, type TraceRow } from "./adminApi";

/** Message trace, outbound queue, suppression list, sending health (PROMPT 7.5). */
export function OperationsAdmin() {
  const { t } = useTranslation();
  const [view, setView] = useState("trace");
  return (
    <Space direction="vertical" className="bm-full">
      <Segmented value={view} onChange={(v) => setView(String(v))}
        options={["trace", "queue", "suppressions", "health"].map((v) => ({ value: v, label: t(`admin.ops.${v}`) }))} />
      {view === "trace" && <Trace />}
      {view === "queue" && <Queue />}
      {view === "suppressions" && <Suppressions />}
      {view === "health" && <Health />}
    </Space>
  );
}

const statusColor = (s: string) => (/DELIVERED|SENT/.test(s) ? "green" : /DEFER|QUEUED|QUARANT/.test(s) ? "orange" : /REJECT|BOUNCE|EXPIRED|CANCEL/.test(s) ? "red" : "default");

function Trace() {
  const { t } = useTranslation();
  const fmt = useUserFormat();
  const [filter, setFilter] = useState({ sender: "", recipient: "", messageId: "" });
  const [applied, setApplied] = useState(filter);
  const [range, setRange] = useState<[Dayjs | null, Dayjs | null] | null>(null);
  const [page, setPage] = useState(1);
  const [open, setOpen] = useState<number | null>(null);
  const rows = useQuery({
    queryKey: ["admin-trace", applied, range?.[0]?.toISOString(), range?.[1]?.toISOString(), page],
    queryFn: () => adminApi.trace({ ...applied, from: range?.[0]?.startOf("day").toISOString(), to: range?.[1]?.endOf("day").toISOString(), page }),
  });
  return (
    <>
      <Space wrap>
        {(["sender", "recipient", "messageId"] as const).map((k) => (
          <Input key={k} placeholder={t(`admin.trace.${k}`)} aria-label={t(`admin.trace.${k}`)} value={filter[k]} allowClear
            onChange={(e) => setFilter({ ...filter, [k]: e.target.value })} onPressEnter={() => { setApplied(filter); setPage(1); }} />
        ))}
        <DatePicker.RangePicker value={range} onChange={(v) => { setRange(v); setPage(1); }} aria-label={t("admin.trace.period")} />
        <Button type="primary" onClick={() => { setApplied(filter); setPage(1); }}>{t("admin.trace.search")}</Button>
      </Space>
      <Table<TraceRow> rowKey="id" size="small" loading={rows.isPending} dataSource={rows.data?.items ?? []} scroll={{ x: true }}
        onRow={(r) => ({ onClick: () => setOpen(r.id), style: { cursor: "pointer" } })}
        pagination={{ current: page, pageSize: 50, total: rows.data?.total ?? 0, onChange: setPage,
          showTotal: (total, range) => t("common.rangeOfTotal", { from: range[0], to: range[1], total }) }}
        columns={[
          { title: t("admin.trace.at"), render: (_, r) => dateTime(r.at, fmt) },
          { title: t("admin.trace.direction"), render: (_, r) => t(`admin.trace.dir.${r.direction}`) },
          { title: t("admin.trace.sender"), render: (_, r) => r.envelopeFrom || <Typography.Text type="secondary" title={t("admin.trace.nullSender")}>&lt;&gt;</Typography.Text> },
          { title: t("admin.trace.recipient"), dataIndex: "rcpt" },
          { title: t("admin.trace.status"), render: (_, r) => <Tag color={statusColor(r.status)}>{t(`admin.trace.st.${r.status}`, { defaultValue: r.status })}</Tag> },
          { title: t("admin.spam.score"), render: (_, r) => r.spamScore?.toFixed(1) },
        ]} />
      {open !== null && <TraceDrawer id={open} onClose={() => setOpen(null)} />}
    </>
  );
}

function TraceDrawer({ id, onClose }: { id: number; onClose: () => void }) {
  const { t } = useTranslation();
  const fmt = useUserFormat();
  const d = useQuery({ queryKey: ["admin-trace-detail", id], queryFn: () => adminApi.traceDetail(id) });
  const r = d.data?.row;
  return (
    <Drawer open width="min(860px, 100vw)" title={r?.messageId ?? t("admin.ops.trace")} onClose={onClose}>
      {r && d.data && (
        <Space direction="vertical" className="bm-full">
          <Descriptions size="small" column={1} bordered items={[
            { key: "at", label: t("admin.trace.at"), children: dateTime(r.at, fmt) },
            { key: "from", label: t("admin.trace.sender"), children: r.envelopeFrom || t("admin.trace.nullSender") },
            { key: "to", label: t("admin.trace.recipient"), children: r.rcpt },
            { key: "status", label: t("admin.trace.status"), children: <Tag color={statusColor(r.status)}>{r.status}</Tag> },
            { key: "remote", label: t("admin.trace.remote"), children: [r.remoteHost, r.remoteIp].filter(Boolean).join(" ") },
            { key: "smtp", label: t("admin.trace.smtp"), children: <code>{r.smtpResponse}</code> },
            { key: "size", label: t("admin.trace.size"), children: r.size.toLocaleString() },
          ]} />
          {d.data.storedIn.map((s) => (
            <Descriptions key={s.mailbox} size="small" column={1} bordered title={t("admin.trace.storedIn", { mailbox: s.mailbox })} items={[
              { key: "labels", label: t("admin.trace.labels"), children: s.labels.join(", ") },
              { key: "auth", label: t("admin.trace.auth"), children: <pre className="bm-pre">{JSON.stringify(s.authResults, null, 2)}</pre> },
              { key: "spam", label: t("admin.spam.score"), children: s.spamScore?.toFixed(1) },
            ]} />
          ))}
          {d.data.queue.length > 0 && <QueueTable items={d.data.queue} />}
          {d.data.details != null && <pre className="bm-pre">{JSON.stringify(d.data.details, null, 2)}</pre>}
        </Space>
      )}
    </Drawer>
  );
}

function QueueTable({ items, selected, onSelect, loading, pagination }: {
  items: QueueItem[]; selected?: number[]; onSelect?: (ids: number[]) => void; loading?: boolean; pagination?: false | object;
}) {
  const { t } = useTranslation();
  const fmt = useUserFormat();
  return (
    <Table<QueueItem> rowKey="id" size="small" loading={loading} dataSource={items} scroll={{ x: true }} pagination={pagination ?? false}
      rowSelection={onSelect ? { selectedRowKeys: selected, onChange: (k) => onSelect(k as number[]) } : undefined}
      columns={[
        { title: t("admin.trace.recipient"), dataIndex: "rcpt" },
        { title: t("admin.trace.sender"), dataIndex: "envelopeFrom" },
        { title: t("admin.trace.status"), render: (_, q) => <Tag color={statusColor(q.status)}>{t(`admin.queue.st.${q.status}`, { defaultValue: q.status })}</Tag> },
        { title: t("admin.queue.attempts"), dataIndex: "attempts" },
        { title: t("admin.queue.next"), render: (_, q) => dateTime(q.nextAttemptAt, fmt) },
        { title: t("admin.queue.last"), render: (_, q) => <Typography.Text type="secondary" className="bm-query">{q.lastResponse}</Typography.Text> },
      ]} />
  );
}

function Queue() {
  const { t } = useTranslation();
  const { message } = App.useApp();
  const qc = useQueryClient();
  const canEdit = useCan(AdminPermission.QueueEdit);
  const [status, setStatus] = useState("PENDING");
  const [domain, setDomain] = useState("");
  const [page, setPage] = useState(1);
  const [selected, setSelected] = useState<number[]>([]);
  const items = useQuery({ queryKey: ["admin-queue", status, domain, page], queryFn: () => adminApi.queue(status, domain, page) });
  const act = (p: Promise<number>, key: string) => void p.then(
    (n) => { void message.success(t(key, { count: n })); setSelected([]); return qc.invalidateQueries({ queryKey: ["admin-queue"] }); },
    (e: unknown) => void message.error(e instanceof ApiError ? e.title : t("errors.generic")));
  return (
    <>
      <Space wrap>
        <Select value={status} onChange={(s) => { setStatus(s); setPage(1); }} className="bm-filter-select" aria-label={t("admin.trace.status")}
          options={["PENDING", "QUEUED", "DEFERRED", "BOUNCED", "ALL"].map((s) => ({ value: s, label: t(`admin.queue.filter.${s}`) }))} />
        <Input placeholder={t("admin.queue.domain")} aria-label={t("admin.queue.domain")} allowClear onChange={(e) => { setDomain(e.target.value); setPage(1); }} />
        {canEdit && <Button disabled={!selected.length} onClick={() => act(adminApi.retry(selected), "admin.queue.retried")}>{t("admin.queue.retry")}</Button>}
        {canEdit && <Button danger disabled={!selected.length} onClick={() => act(adminApi.cancel(selected), "admin.queue.cancelled")}>{t("admin.queue.cancel")}</Button>}
      </Space>
      <QueueTable items={items.data?.items ?? []} loading={items.isPending} selected={selected} onSelect={canEdit ? setSelected : undefined}
        pagination={{ current: page, pageSize: 50, total: items.data?.total ?? 0, onChange: setPage }} />
    </>
  );
}

function Suppressions() {
  const { t } = useTranslation();
  const { message } = App.useApp();
  const fmt = useUserFormat();
  const qc = useQueryClient();
  const canEdit = useCan(AdminPermission.QueueEdit);
  const [search, setSearch] = useState("");
  const [page, setPage] = useState(1);
  const items = useQuery({ queryKey: ["admin-suppressions", search, page], queryFn: () => adminApi.suppressions(search, page) });
  return (
    <>
      <Input.Search placeholder={t("admin.suppressions.search")} aria-label={t("admin.suppressions.search")} allowClear onSearch={(v) => { setSearch(v); setPage(1); }} />
      <Table rowKey="id" size="small" loading={items.isPending} dataSource={items.data?.items ?? []} scroll={{ x: true }}
        pagination={{ current: page, pageSize: 50, total: items.data?.total ?? 0, onChange: setPage }}
        columns={[
          { title: t("admin.suppressions.address"), dataIndex: "address" },
          { title: t("admin.suppressions.reason"), render: (_, s) => <Tag>{t(`admin.suppressions.r.${s.reason}`, { defaultValue: s.reason })}</Tag> },
          { title: t("admin.suppressions.detail"), render: (_, s) => <Typography.Text type="secondary">{s.detail}</Typography.Text> },
          { title: t("admin.quarantine.at"), render: (_, s) => dateTime(s.createdAt, fmt) },
          { title: <span className="bm-visually-hidden">{t("common.actions")}</span>, render: (_, s) => canEdit &&
            <Button size="small" onClick={() => void adminApi.removeSuppression(s.id).then(() => qc.invalidateQueries({ queryKey: ["admin-suppressions"] }),
              (e: unknown) => void message.error(e instanceof ApiError ? e.title : t("errors.generic")))}>{t("admin.suppressions.remove")}</Button> },
        ]} />
    </>
  );
}

function Health() {
  const { t, i18n } = useTranslation();
  const [days, setDays] = useState(7);
  const h = useQuery({ queryKey: ["admin-health", days], queryFn: () => adminApi.health(days) });
  const pct = (n: number) => (n * 100).toLocaleString(i18n.language, { maximumFractionDigits: 1 }) + "%";
  return (
    <>
      <Segmented value={days} onChange={(v) => setDays(Number(v))} options={[1, 7, 30].map((d) => ({ value: d, label: t("admin.health.days", { count: d }) }))} />
      {h.data && (
        <>
          <Space wrap size="large">
            <Statistic title={t("admin.health.delivered")} value={h.data.delivered} />
            <Statistic title={t("admin.health.bounced")} value={h.data.bounced} />
            <Statistic title={t("admin.health.bounceRate")} value={pct(h.data.bounceRate)} />
            <Statistic title={t("admin.health.complaints")} value={h.data.complaints} />
          </Space>
          <Table rowKey="domain" size="small" pagination={{ pageSize: 20 }} dataSource={h.data.domains} scroll={{ x: true }}
            columns={[
              { title: t("admin.queue.domain"), dataIndex: "domain" },
              { title: t("admin.health.delivered"), dataIndex: "delivered" },
              { title: t("admin.health.deferred"), dataIndex: "deferred" },
              { title: t("admin.health.bounced"), dataIndex: "bounced" },
              { title: t("admin.health.rate"), render: (_, d) => pct(d.deliveryRate) },
            ]} />
          <Typography.Title level={5}>{t("admin.health.ipLists")}</Typography.Title>
          <Table rowKey={(r) => r.ip + r.list} size="small" pagination={false} dataSource={h.data.ipListings}
            locale={{ emptyText: t("admin.health.noIps") }}
            columns={[
              { title: "IP", dataIndex: "ip" },
              { title: t("admin.spam.list"), dataIndex: "list" },
              { title: t("admin.trace.status"), render: (_, r) => r.listed === null ? <Tag>{t("admin.health.unknown")}</Tag>
                : r.listed ? <Tag color="red">{t("admin.health.listed")}</Tag> : <Tag color="green">{t("admin.health.clean")}</Tag> },
            ]} />
        </>
      )}
    </>
  );
}
