import { useState } from "react";
import { App, Button, DatePicker, Select, Space, Table, Typography } from "antd";
import { DownloadOutlined, PrinterOutlined } from "@ant-design/icons";
import { useQuery } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import type { Dayjs } from "dayjs";
import { ApiError } from "../../api/client";
import { date } from "../../format";
import { useUserFormat } from "../../session";
import { adminApi, download, type Report } from "./adminApi";

const kinds: string[] = ["MAIL_BY_DAY", "SPAM_RATE", "STORAGE_BY_USER", "USERS_WITHOUT_2FA"];
// Which column a report charts (the first column is the category axis). Reports without one show only the table.
const chartColumn: Record<string, string> = { MAIL_BY_DAY: "received", SPAM_RATE: "spam_percent", STORAGE_BY_USER: "used_mb" };

/** Reports (PROMPT 7.5): table + chart, XLSX export, PDF through the browser's print dialog (docs/00 D19). */
export function ReportsAdmin() {
  const { t } = useTranslation();
  const { message } = App.useApp();
  const fmt = useUserFormat();
  const [kind, setKind] = useState<string>("MAIL_BY_DAY");
  const [range, setRange] = useState<[Dayjs | null, Dayjs | null] | null>(null);
  const from = range?.[0]?.startOf("day").toISOString() ?? null;
  const to = range?.[1]?.endOf("day").toISOString() ?? null;
  const report = useQuery({ queryKey: ["admin-report", kind, from, to], queryFn: () => adminApi.report(kind, from, to) });
  const exportXlsx = async () => {
    try {
      download(await adminApi.reportXlsx(kind, from, to), `${kind.toLowerCase()}.xlsx`);
    } catch (e) {
      void message.error(e instanceof ApiError ? e.title : t("errors.generic"));
    }
  };
  const r = report.data;

  return (
    <Space direction="vertical" className="bm-full bm-report">
      <Space wrap className="bm-no-print">
        <Select value={kind} onChange={setKind} className="bm-report-select" aria-label={t("admin.reports.kind")}
          options={kinds.map((k) => ({ value: k, label: t(`admin.reports.k.${k}`) }))} />
        <DatePicker.RangePicker value={range} onChange={setRange} aria-label={t("admin.trace.period")} />
        <Button icon={<DownloadOutlined />} onClick={() => void exportXlsx()}>{t("admin.reports.xlsx")}</Button>
        <Button icon={<PrinterOutlined />} onClick={() => window.print()}>{t("admin.reports.print")}</Button>
      </Space>
      {r && (
        <>
          <Typography.Title level={4}>{t(`admin.reports.k.${r.kind}`)}</Typography.Title>
          <Typography.Text type="secondary">
            {t("admin.reports.period", { from: date(r.from, fmt), to: date(r.to, fmt) })} · {r.truncated
              ? t("admin.reports.truncated", { limit: r.limit }) : t("admin.reports.complete", { count: r.rows.length })}
          </Typography.Text>
          {chartColumn[r.kind] !== undefined && r.rows.length > 0 && <BarChart report={r} column={chartColumn[r.kind] ?? ""} />}
          <Table rowKey={(_, i) => String(i)} size="small" dataSource={r.rows} pagination={{ pageSize: 50, hideOnSinglePage: true }} scroll={{ x: true }}
            columns={r.columns.map((c, i) => ({
              title: t(`admin.reports.col.${c}`),
              align: typeof r.rows[0]?.[i] === "number" ? ("right" as const) : undefined,
              render: (_: unknown, row: Report["rows"][number]) => cell(row[i] ?? null, c, t),
            }))} />
        </>
      )}
    </Space>
  );
}

function cell(v: string | number | boolean | null, column: string, t: (k: string) => string) {
  if (v === null) return "";
  if (typeof v === "boolean") return t(v ? "admin.domains.yes" : "admin.domains.no");
  if (typeof v === "number") return v.toLocaleString(undefined, { maximumFractionDigits: column.endsWith("percent") ? 1 : 0 });
  return v;
}

/** A plain SVG bar chart: one series, labelled axis, values readable from the table underneath. */
function BarChart({ report, column }: { report: Report; column: string }) {
  const { t } = useTranslation();
  const ci = report.columns.indexOf(column);
  const rows = report.rows.slice(0, 60);
  const values = rows.map((r) => Number(r[ci] ?? 0));
  const max = Math.max(1, ...values);
  const w = 720, h = 220, left = 48, bottom = 28, top = 8;
  const bw = (w - left) / rows.length;
  const barW = Math.max(1, Math.min(bw * 0.7, 36)); // a lone bar stays a bar, not a block
  const ticks = [0, max / 2, max];
  return (
    <figure className="bm-chart">
      <svg viewBox={`0 0 ${w} ${h}`} role="img" aria-label={t("admin.reports.chartLabel", { column: t(`admin.reports.col.${column}`) })}>
        {ticks.map((v) => {
          const y = top + (h - top - bottom) * (1 - v / max);
          return (
            <g key={v}>
              <line x1={left} x2={w} y1={y} y2={y} className="bm-chart-grid" />
              <text x={left - 6} y={y + 4} textAnchor="end" className="bm-chart-tick">{v.toLocaleString(undefined, { maximumFractionDigits: 1 })}</text>
            </g>
          );
        })}
        {values.map((v, i) => {
          const bh = (h - top - bottom) * (v / max);
          return (
            <rect key={i} x={left + i * bw + (bw - barW) / 2} y={h - bottom - bh} width={barW} height={bh} rx={2} className="bm-chart-bar">
              <title>{`${String(rows[i]?.[0])}: ${v.toLocaleString()}`}</title>
            </rect>
          );
        })}
        {rows.map((r, i) => (i % Math.ceil(rows.length / 8) === 0 ? (
          <text key={i} x={left + i * bw + bw / 2} y={h - 8} textAnchor="middle" className="bm-chart-tick">{String(r[0]).slice(0, 12)}</text>
        ) : null))}
      </svg>
      <figcaption className="bm-visually-hidden">{t(`admin.reports.col.${column}`)}</figcaption>
    </figure>
  );
}
