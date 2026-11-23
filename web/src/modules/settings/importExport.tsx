import { useState } from "react";
import { Alert, App, Button, Form, Input, InputNumber, Progress, Select, Space, Table, Tag, Typography, Upload } from "antd";
import { DownloadOutlined, UploadOutlined } from "@ant-design/icons";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { api, ApiError } from "../../api/client";
import { uploadFile } from "../../api/uploads";
import { config } from "../../config";
import { bytes, dateTime } from "../../format";
import { useUserFormat } from "../../session";

// Moving mail in and data out (PROMPT 7.7): IMAP and file imports, "download my data".

export interface ImapSource { host: string; port: number; tls: "IMPLICIT" | "STARTTLS"; username: string; password: string }
export interface ImportJob {
  id: string; userId: string; userEmail: string | null; source: string; host: string | null; username: string | null; fileName: string | null; status: string;
  total: number; imported: number; skipped: number; failed: number; currentFolder: string | null; error: string | null; createdAt: string; finishedAt: string | null;
  batchId: string | null;
}
export interface DataExport {
  id: string; kind: string; subject: string | null; query: string | null; format: string; status: string; total: number; done: number; sizeBytes: number;
  sha256: string | null; error: string | null; createdAt: string; finishedAt: string | null; expiresAt: string | null;
}
interface TestResult { ok: boolean; error: string | null; folders: number; messages: number; gmail: boolean }

export const migrationApi = {
  test: (s: ImapSource) => api.post<TestResult>("/api/v1/me/imports/test", s),
  startMine: (s: ImapSource) => api.post<string>("/api/v1/me/imports", s),
  startFile: (uploadId: string) => api.post<string>("/api/v1/me/imports/file", { uploadId }),
  mine: () => api.get<ImportJob[]>("/api/v1/me/imports"),
  cancel: (id: string) => api.post<boolean>(`/api/v1/me/imports/${id}/cancel`, {}),
  errors: (id: string) => api.blob(`/api/v1/me/imports/${id}/errors.csv`),
  startFor: (userId: string, source: ImapSource) => api.post<string>("/api/v1/admin/imports", { userId, source }),
  batch: (file: File) => {
    const f = new FormData();
    f.append("file", file);
    return api.form<{ batchId: string | null; created: number; errors: { row: number; field: string; code: string }[] }>("/api/v1/admin/imports/batch", f);
  },
  all: () => api.get<ImportJob[]>("/api/v1/admin/imports"),
  requestMyExport: () => api.post<string>("/api/v1/me/export", {}),
  myExports: () => api.get<DataExport[]>("/api/v1/me/exports"),
  link: (id: string) => api.post<string>(`/api/v1/exports/${id}/link`, {}),
};

const running = (s: string) => s === "QUEUED" || s === "RUNNING";

export function errorText(t: (k: "errors.generic") => string, e: unknown) {
  return e instanceof ApiError ? e.title : t("errors.generic");
}

/** IMAP source form with a "test" step; known providers fill in the server. */
export function ImapImportForm({ onStart, extra }: { onStart: (s: ImapSource) => Promise<unknown>; extra?: React.ReactNode }) {
  const { t } = useTranslation();
  const { message } = App.useApp();
  const [form] = Form.useForm<ImapSource>();
  const [test, setTest] = useState<TestResult | null>(null);
  const [busy, setBusy] = useState(false);
  const presets: Record<string, Partial<ImapSource>> = {
    gmail: { host: "imap.gmail.com", port: 993, tls: "IMPLICIT" },
    outlook: { host: "outlook.office365.com", port: 993, tls: "IMPLICIT" },
    yahoo: { host: "imap.mail.yahoo.com", port: 993, tls: "IMPLICIT" },
  };
  const run = async (fn: () => Promise<void>) => {
    setBusy(true);
    try {
      await fn();
    } catch (e) {
      void message.error(errorText(t, e));
    } finally {
      setBusy(false);
    }
  };
  return (
    <Form<ImapSource> form={form} layout="vertical" initialValues={{ port: 993, tls: "IMPLICIT" }} className="bm-settings-form" onValuesChange={() => setTest(null)}>
      {extra}
      <Form.Item label={t("import.provider")}>
        <Select placeholder={t("import.providerHint")} aria-label={t("import.provider")} onChange={(k: string) => form.setFieldsValue(presets[k] ?? {})}
          options={[{ value: "gmail", label: "Gmail" }, { value: "outlook", label: "Outlook.com / Microsoft 365" }, { value: "yahoo", label: "Yahoo" },
            { value: "other", label: t("import.otherServer") }]} />
      </Form.Item>
      <Space.Compact className="bm-full">
        <Form.Item name="host" label={t("import.host")} rules={[{ required: true }]} className="bm-grow"><Input autoComplete="off" /></Form.Item>
        <Form.Item name="port" label={t("import.port")} rules={[{ required: true }]}><InputNumber min={1} max={65535} /></Form.Item>
        <Form.Item name="tls" label={t("import.security")}>
          <Select options={[{ value: "IMPLICIT", label: "SSL/TLS" }, { value: "STARTTLS", label: "STARTTLS" }]} />
        </Form.Item>
      </Space.Compact>
      <Form.Item name="username" label={t("import.username")} rules={[{ required: true }]}><Input autoComplete="off" /></Form.Item>
      <Form.Item name="password" label={t("import.password")} extra={t("import.appPasswordHint")} rules={[{ required: true }]}>
        <Input.Password autoComplete="new-password" />
      </Form.Item>
      {test && (test.ok
        ? <Alert type="success" showIcon message={t("import.testOk", { folders: test.folders, messages: test.messages })} description={test.gmail ? t("import.gmailNote") : undefined} />
        : <Alert type="error" showIcon message={t(`importErrors.${test.error ?? "import_failed"}`)} />)}
      <Space className="bm-settings-gap">
        <Button loading={busy} onClick={() => void run(async () => setTest(await migrationApi.test(await form.validateFields())))}>{t("import.test")}</Button>
        <Button type="primary" loading={busy} disabled={!test?.ok} onClick={() => void run(async () => {
          await onStart(await form.validateFields());
          form.resetFields(["password"]);
          setTest(null);
          void message.success(t("import.started"));
        })}>{t("import.start")}</Button>
      </Space>
    </Form>
  );
}

export function ImportJobsTable({ queryKey, load, showUser }: { queryKey: string[]; load: () => Promise<ImportJob[]>; showUser?: boolean }) {
  const { t } = useTranslation();
  const { message } = App.useApp();
  const fmt = useUserFormat();
  const qc = useQueryClient();
  const jobs = useQuery({ queryKey, queryFn: load, refetchInterval: (q) => ((q.state.data ?? []).some((j) => running(j.status)) ? 2000 : false) });
  const downloadErrors = async (j: ImportJob) => {
    const blob = await migrationApi.errors(j.id);
    const a = document.createElement("a");
    a.href = URL.createObjectURL(blob);
    a.download = `import-errors-${j.id.slice(0, 8)}.csv`;
    a.click();
    URL.revokeObjectURL(a.href);
  };
  return (
    <Table<ImportJob> rowKey="id" size="small" loading={jobs.isPending} dataSource={jobs.data ?? []} pagination={{ pageSize: 20, hideOnSinglePage: true }} scroll={{ x: true }}
      locale={{ emptyText: t("import.none") }}
      columns={[
        ...(showUser ? [{ title: t("adminUsers.email"), dataIndex: "userEmail" }] : []),
        { title: t("import.from"), render: (_, j) => j.source === "IMAP" ? `${j.username} @ ${j.host}` : j.fileName },
        { title: t("admin.trace.at"), render: (_, j) => dateTime(j.createdAt, fmt) },
        { title: t("admin.trace.status"), render: (_, j) => (
          <Space direction="vertical" size={0}>
            <Tag color={j.status === "DONE" ? "green" : j.status === "FAILED" ? "red" : running(j.status) ? "blue" : "default"}>{t(`import.status.${j.status}`)}</Tag>
            {running(j.status) && j.total > 0 && <Progress percent={Math.round(((j.imported + j.skipped + j.failed) / j.total) * 100)} size="small" />}
            {j.currentFolder && running(j.status) && <Typography.Text type="secondary">{j.currentFolder}</Typography.Text>}
            {j.error && <Typography.Text type="danger">{t(`importErrors.${j.error}`)}</Typography.Text>}
          </Space>
        ) },
        { title: t("import.counts"), render: (_, j) => t("import.countsValue", { imported: j.imported, skipped: j.skipped, failed: j.failed }) },
        { title: <span className="bm-visually-hidden">{t("common.actions")}</span>, render: (_, j) => (
          <Space>
            {running(j.status) && <Button size="small" onClick={() => void migrationApi.cancel(j.id).then(() => qc.invalidateQueries({ queryKey }), (e: unknown) => void message.error(errorText(t, e)))}>
              {t("common.cancel")}</Button>}
            {j.failed > 0 && <Button size="small" icon={<DownloadOutlined />} onClick={() => void downloadErrors(j)}>{t("import.errorsCsv")}</Button>}
          </Space>
        ) },
      ]} />
  );
}

export async function downloadExport(id: string) {
  const link = await migrationApi.link(id);
  window.location.assign(config().apiBase + link);
}

export function ExportsTable({ queryKey, load }: { queryKey: string[]; load: () => Promise<DataExport[]> }) {
  const { t, i18n } = useTranslation();
  const { message } = App.useApp();
  const fmt = useUserFormat();
  const exports = useQuery({ queryKey, queryFn: load, refetchInterval: (q) => ((q.state.data ?? []).some((e) => running(e.status)) ? 2000 : false) });
  const expired = (e: DataExport) => e.expiresAt !== null && new Date(e.expiresAt) < new Date();
  return (
    <Table<DataExport> rowKey="id" size="small" loading={exports.isPending} dataSource={exports.data ?? []} pagination={{ pageSize: 20, hideOnSinglePage: true }}
      scroll={{ x: true }} locale={{ emptyText: t("export.none") }}
      columns={[
        { title: t("export.kind"), render: (_, e) => <Space direction="vertical" size={0}>{t(`export.kinds.${e.kind}`)}
          {e.query && <code className="bm-query">{e.query}</code>}{e.subject && <Typography.Text type="secondary">{e.subject}</Typography.Text>}</Space> },
        { title: t("admin.trace.at"), render: (_, e) => dateTime(e.createdAt, fmt) },
        { title: t("admin.trace.status"), render: (_, e) => <Tag color={e.status === "DONE" && !expired(e) ? "green" : e.status === "FAILED" ? "red" : "blue"}>
          {expired(e) ? t("export.expired") : t(`import.status.${e.status}`)}{running(e.status) && e.done > 0 ? ` (${e.done})` : ""}</Tag> },
        { title: t("export.size"), render: (_, e) => (e.status === "DONE" ? bytes(e.sizeBytes, i18n.language) : "") },
        { title: <span className="bm-visually-hidden">{t("common.actions")}</span>, render: (_, e) => e.status === "DONE" && !expired(e) && (
          <Space direction="vertical" size={0}>
            <Button size="small" icon={<DownloadOutlined />} onClick={() => void downloadExport(e.id).catch((x: unknown) => void message.error(errorText(t, x)))}>
              {t("export.download")}</Button>
            {e.expiresAt && <Typography.Text type="secondary">{t("export.until", { at: dateTime(e.expiresAt, fmt) })}</Typography.Text>}
            {e.sha256 && <Typography.Text type="secondary" className="bm-query" title="SHA-256">{e.sha256.slice(0, 16)}…</Typography.Text>}
          </Space>
        ) },
      ]} />
  );
}

/** Settings → "Import and export". */
export function ImportExportSettings() {
  const { t } = useTranslation();
  const { message } = App.useApp();
  const qc = useQueryClient();
  const [progress, setProgress] = useState<number | null>(null);
  const refresh = () => qc.invalidateQueries({ queryKey: ["my-imports"] });
  const upload = (file: File) => {
    setProgress(0);
    uploadFile(file, {
      progress: (sent, total) => setProgress(Math.round((sent / total) * 100)),
      done: (id) => {
        setProgress(null);
        void migrationApi.startFile(id).then(() => { void message.success(t("import.started")); return refresh(); }, (e: unknown) => void message.error(errorText(t, e)));
      },
      error: () => { setProgress(null); void message.error(t("errors.generic")); },
    });
    return false;
  };
  return (
    <Space direction="vertical" className="bm-full">
      <Typography.Title level={5}>{t("import.title")}</Typography.Title>
      <Typography.Paragraph type="secondary">{t("import.intro")}</Typography.Paragraph>
      <ImapImportForm onStart={(s) => migrationApi.startMine(s).then(refresh)} />
      <Typography.Title level={5} className="bm-settings-gap">{t("import.fileTitle")}</Typography.Title>
      <Typography.Paragraph type="secondary">{t("import.fileHint")}</Typography.Paragraph>
      <Upload accept=".mbox,.mbx,.eml,.zip" showUploadList={false} beforeUpload={upload}>
        <Button icon={<UploadOutlined />} loading={progress !== null}>{t("import.chooseFile")}</Button>
      </Upload>
      {progress !== null && <Progress percent={progress} />}
      <ImportJobsTable queryKey={["my-imports"]} load={migrationApi.mine} />
      <Typography.Title level={5} className="bm-settings-gap">{t("export.title")}</Typography.Title>
      <Typography.Paragraph type="secondary">{t("export.intro")}</Typography.Paragraph>
      <Button onClick={() => void migrationApi.requestMyExport().then(() => qc.invalidateQueries({ queryKey: ["my-exports"] }), (e: unknown) => void message.error(errorText(t, e)))}>
        {t("export.request")}</Button>
      <ExportsTable queryKey={["my-exports"]} load={migrationApi.myExports} />
    </Space>
  );
}
