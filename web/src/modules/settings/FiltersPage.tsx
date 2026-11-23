import { useEffect, useRef, useState } from "react";
import { useSearchParams } from "react-router-dom";
import { Alert, App, Button, Checkbox, Empty, Input, List, Modal, Progress, Select, Space, Switch, Tabs, Tag, Typography, Upload } from "antd";
import { ArrowDownOutlined, ArrowUpOutlined, DeleteOutlined, DownloadOutlined, EditOutlined, PlusOutlined, UploadOutlined } from "@ant-design/icons";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { api, ApiError } from "../../api/client";
import type { Labels } from "../../api/types";
import { mailKeys } from "../mail/mailApi";
import { userLabels } from "../mail/labels";
import { hasAction, noActions, rulesApi, rulesKeys, type Filter, type FilterActions, type FilterJob } from "./rulesApi";

/** Filters and blocked senders (PROMPT 6.8). Filters run on the server for every delivery. */
export function FiltersPage() {
  const { t } = useTranslation();
  return (
    <section aria-labelledby="filters-title">
      <Typography.Title level={4} id="filters-title">{t("filters.title")}</Typography.Title>
      <Tabs items={[
        { key: "filters", label: t("filters.filters"), children: <Filters /> },
        { key: "blocked", label: t("filters.blocked"), children: <Blocked /> },
      ]} />
    </section>
  );
}

interface Editing {
  id: string | null;
  criteria: string;
  actions: FilterActions;
  isEnabled: boolean;
}

function Filters() {
  const { t } = useTranslation();
  const { message, modal } = App.useApp();
  const qc = useQueryClient();
  const [params, setParams] = useSearchParams();
  const filters = useQuery({ queryKey: rulesKeys.filters, queryFn: rulesApi.filters });
  const [edit, setEdit] = useState<Editing | null>(null);
  const [problems, setProblems] = useState<string[]>([]);

  // "Create filter" from the advanced search arrives as ?criteria=...
  useEffect(() => {
    const c = params.get("criteria");
    if (c) {
      setEdit({ id: null, criteria: c, actions: noActions, isEnabled: true });
      setParams((p) => { p.delete("criteria"); return p; }, { replace: true });
    }
  }, [params, setParams]);

  const refresh = () => qc.invalidateQueries({ queryKey: rulesKeys.filters });
  const move = async (index: number, delta: number) => {
    const list = [...(filters.data ?? [])];
    const [f] = list.splice(index, 1);
    list.splice(index + delta, 0, f!);
    await rulesApi.reorder(list.map((x) => x.id));
    await refresh();
  };

  const exportXml = async () => {
    const blob = await rulesApi.exportXml();
    const url = URL.createObjectURL(blob);
    const a = document.createElement("a");
    a.href = url;
    a.download = "mailFilters.xml";
    a.click();
    URL.revokeObjectURL(url);
  };

  const importXml = async (file: File) => {
    try {
      const r = await rulesApi.importXml(file);
      message.success(t("filters.imported", { count: r.imported }));
      setProblems(r.problems.map((p) => t("filters.importProblem", { entry: p.entry, reason: t(`filters.problem.${p.code}`, { defaultValue: p.code }), detail: p.detail ?? "" })));
      await refresh();
    } catch (e) {
      message.error(e instanceof ApiError ? e.title : t("errors.generic"));
    }
    return false;
  };

  return (
    <>
      <Space wrap>
        <Button type="primary" icon={<PlusOutlined />} onClick={() => setEdit({ id: null, criteria: "", actions: noActions, isEnabled: true })}>{t("filters.new")}</Button>
        <Upload accept=".xml,application/xml,text/xml" showUploadList={false} beforeUpload={(f) => void importXml(f) as unknown as boolean}>
          <Button icon={<UploadOutlined />}>{t("filters.import")}</Button>
        </Upload>
        <Button icon={<DownloadOutlined />} onClick={() => void exportXml()} disabled={!filters.data?.length}>{t("filters.export")}</Button>
      </Space>
      {problems.length > 0 && (
        <Alert className="bm-banner bm-settings-gap" type="warning" showIcon closable onClose={() => setProblems([])} message={t("filters.importProblems")}
          description={<ul>{problems.map((p, i) => <li key={i}>{p}</li>)}</ul>} />
      )}
      {filters.data && filters.data.length === 0 && <Empty description={t("filters.none")} />}
      <List className="bm-settings-list" dataSource={filters.data ?? []} renderItem={(f, i) => (
        <List.Item actions={[
          <Button key="up" type="text" icon={<ArrowUpOutlined />} disabled={i === 0} aria-label={t("filters.moveUp")} onClick={() => void move(i, -1)} />,
          <Button key="down" type="text" icon={<ArrowDownOutlined />} disabled={i === (filters.data?.length ?? 0) - 1} aria-label={t("filters.moveDown")}
            onClick={() => void move(i, 1)} />,
          <Button key="e" type="text" icon={<EditOutlined />} aria-label={t("filters.edit")} onClick={() => setEdit({ ...f })} />,
          <Button key="d" type="text" danger icon={<DeleteOutlined />} aria-label={t("filters.delete")}
            onClick={() => modal.confirm({ title: t("filters.deleteConfirm"), okButtonProps: { danger: true },
              onOk: async () => { await rulesApi.deleteFilter(f.id); await refresh(); } })} />,
        ]}>
          <List.Item.Meta
            title={<code className="bm-query">{f.criteria}</code>}
            description={<ActionSummary f={f} />}
          />
          {!f.isEnabled && <Tag>{t("filters.disabled")}</Tag>}
        </List.Item>
      )} />
      {edit && <FilterEditor value={edit} onClose={() => setEdit(null)} onSaved={() => { setEdit(null); void refresh(); }} />}
    </>
  );
}

function ActionSummary({ f }: { f: Filter }) {
  const { t } = useTranslation();
  const labels = useQuery({ queryKey: mailKeys.labels, queryFn: () => api.get<Labels>("/api/v1/mail/labels") });
  const a = f.actions;
  const parts = [
    a.skipInbox && t("filters.a.skipInbox"), a.markRead && t("filters.a.markRead"), a.star && t("filters.a.star"),
    ...a.labelIds.map((id) => t("filters.a.labelNamed", { name: labels.data?.labels.find((l) => l.id === id)?.name ?? "?" })),
    a.forwardTo && t("filters.a.forwardTo", { address: a.forwardTo }), a.trash && t("filters.a.trash"), a.neverSpam && t("filters.a.neverSpam"),
    a.important === "ALWAYS" && t("filters.a.alwaysImportant"), a.important === "NEVER" && t("filters.a.neverImportant"),
    a.category && t("filters.a.categoryNamed", { name: t(`filters.cat.${a.category}`) }),
  ].filter(Boolean);
  return <span>{parts.join(" · ")}</span>;
}

function FilterEditor({ value, onClose, onSaved }: { value: Editing; onClose: () => void; onSaved: () => void }) {
  const { t } = useTranslation();
  const { message } = App.useApp();
  const [edit, setEdit] = useState(value);
  const [error, setError] = useState<string | null>(null);
  const [count, setCount] = useState<number | null>(null);
  const [applyExisting, setApplyExisting] = useState(false);
  const [job, setJob] = useState<FilterJob | null>(null);
  const timer = useRef<number | null>(null);
  const labels = useQuery({ queryKey: mailKeys.labels, queryFn: () => api.get<Labels>("/api/v1/mail/labels") });
  const forwarding = useQuery({ queryKey: rulesKeys.forwarding, queryFn: rulesApi.forwarding });
  const a = edit.actions;
  const set = (patch: Partial<FilterActions>) => setEdit((e) => ({ ...e, actions: { ...e.actions, ...patch } }));

  useEffect(() => () => { if (timer.current) window.clearInterval(timer.current); }, []);

  // Preview: how many conversations match now (also catches broken queries early).
  useEffect(() => {
    const q = edit.criteria.trim();
    setCount(null);
    setError(null);
    if (!q) return;
    const h = window.setTimeout(() => {
      rulesApi.matches(q).then(setCount, (e: unknown) => setError(e instanceof ApiError ? e.title : t("errors.generic")));
    }, 400);
    return () => window.clearTimeout(h);
  }, [edit.criteria, t]);

  const save = async () => {
    try {
      const id = await rulesApi.saveFilter({ ...edit, criteria: edit.criteria.trim() });
      message.success(t("filters.saved"));
      if (applyExisting && count) {
        const started = await rulesApi.apply(id);
        setJob(started);
        timer.current = window.setInterval(() => {
          void rulesApi.job(started.id).then((j) => {
            setJob(j);
            if (j.status === "DONE" || j.status === "FAILED") {
              window.clearInterval(timer.current!);
              timer.current = null;
              if (j.status === "DONE") onSaved();
            }
          });
        }, 1000);
        return;
      }
      onSaved();
    } catch (e) {
      setError(e instanceof ApiError ? e.title : t("errors.generic"));
    }
  };

  const custom = labels.data ? userLabels(labels.data.labels) : [];
  const verified = (forwarding.data?.addresses ?? []).filter((x) => x.verified);

  return (
    <Modal open width="min(680px, 96vw)" title={edit.id ? t("filters.edit") : t("filters.new")} onCancel={onClose} onOk={() => void save()}
      okText={t("filters.save")} okButtonProps={{ disabled: !edit.criteria.trim() || !hasAction(a) || job !== null }}>
      <Space direction="vertical" className="bm-full">
        <label htmlFor="filter-criteria">{t("filters.criteria")}</label>
        <Input.TextArea id="filter-criteria" autoSize={{ minRows: 1, maxRows: 4 }} maxLength={2000} value={edit.criteria} placeholder={t("filters.criteriaHint")}
          onChange={(e) => setEdit({ ...edit, criteria: e.target.value })} status={error ? "error" : undefined} />
        {error && <Typography.Text type="danger" role="alert">{error}</Typography.Text>}
        {count !== null && <Typography.Text type="secondary">{t("filters.matchesNow", { count })}</Typography.Text>}
        <Typography.Text strong>{t("filters.whenMatches")}</Typography.Text>
        <Checkbox checked={a.skipInbox} onChange={(e) => set({ skipInbox: e.target.checked })}>{t("filters.a.skipInbox")}</Checkbox>
        <Checkbox checked={a.markRead} onChange={(e) => set({ markRead: e.target.checked })}>{t("filters.a.markRead")}</Checkbox>
        <Checkbox checked={a.star} onChange={(e) => set({ star: e.target.checked })}>{t("filters.a.star")}</Checkbox>
        <Space wrap>
          <span>{t("filters.a.label")}</span>
          <Select mode="multiple" className="bm-filter-select" value={a.labelIds} onChange={(v) => set({ labelIds: v })} aria-label={t("filters.a.label")}
            options={custom.map((l) => ({ value: l.id, label: l.name }))} />
        </Space>
        <Space wrap>
          <span>{t("filters.a.forward")}</span>
          <Select className="bm-filter-select" allowClear value={a.forwardTo ?? undefined} onChange={(v?: string) => set({ forwardTo: v ?? null })}
            aria-label={t("filters.a.forward")} placeholder={verified.length ? undefined : t("filters.noVerified")} disabled={!verified.length}
            options={verified.map((x) => ({ value: x.targetEmail, label: x.targetEmail }))} />
        </Space>
        <Checkbox checked={a.trash} onChange={(e) => set({ trash: e.target.checked })}>{t("filters.a.trash")}</Checkbox>
        <Checkbox checked={a.neverSpam} onChange={(e) => set({ neverSpam: e.target.checked })}>{t("filters.a.neverSpam")}</Checkbox>
        <Space wrap>
          <span>{t("filters.a.important")}</span>
          <Select className="bm-filter-select" value={a.important ?? ""} onChange={(v: string) => set({ important: (v || null) as FilterActions["important"] })}
            aria-label={t("filters.a.important")}
            options={[{ value: "", label: t("filters.unchanged") }, { value: "ALWAYS", label: t("filters.a.alwaysImportant") }, { value: "NEVER", label: t("filters.a.neverImportant") }]} />
        </Space>
        <Space wrap>
          <span>{t("filters.a.category")}</span>
          <Select className="bm-filter-select" value={a.category ?? ""} onChange={(v: string) => set({ category: (v || null) as FilterActions["category"] })}
            aria-label={t("filters.a.category")}
            options={[{ value: "", label: t("filters.unchanged") }, ...(["PRIMARY", "SOCIAL", "PROMOTIONS", "UPDATES", "FORUMS"] as const).map((c) => ({ value: c, label: t(`filters.cat.${c}`) }))]} />
        </Space>
        <Switch checked={edit.isEnabled} onChange={(v) => setEdit({ ...edit, isEnabled: v })} checkedChildren={t("filters.enabled")} unCheckedChildren={t("filters.disabled")} />
        {count !== null && count > 0 && (
          <Checkbox checked={applyExisting} onChange={(e) => setApplyExisting(e.target.checked)}>{t("filters.applyExisting", { count })}</Checkbox>
        )}
        {job && (
          <div role="status">
            <Progress percent={job.total ? Math.round((job.done / job.total) * 100) : 0} status={job.status === "FAILED" ? "exception" : undefined} aria-label={t("common.progress")} />
            {t(job.status === "DONE" ? "filters.jobDone" : job.status === "FAILED" ? "filters.jobFailed" : "filters.jobRunning", { done: job.done, total: job.total })}
          </div>
        )}
      </Space>
    </Modal>
  );
}

function Blocked() {
  const { t } = useTranslation();
  const { message } = App.useApp();
  const qc = useQueryClient();
  const blocked = useQuery({ queryKey: rulesKeys.blocked, queryFn: rulesApi.blocked });
  const [value, setValue] = useState("");
  const add = async () => {
    try {
      await rulesApi.block(value.trim());
      setValue("");
      await qc.invalidateQueries({ queryKey: rulesKeys.blocked });
    } catch (e) {
      message.error(e instanceof ApiError ? e.title : t("errors.generic"));
    }
  };
  return (
    <>
      <Typography.Paragraph type="secondary">{t("filters.blockedHint")}</Typography.Paragraph>
      <Space.Compact className="bm-full">
        <Input value={value} maxLength={320} placeholder={t("filters.blockPlaceholder")} aria-label={t("filters.blockPlaceholder")}
          onChange={(e) => setValue(e.target.value)} onPressEnter={() => void add()} />
        <Button type="primary" onClick={() => void add()} disabled={!value.trim()}>{t("filters.block")}</Button>
      </Space.Compact>
      {blocked.data && blocked.data.length === 0 && <Empty description={t("filters.noneBlocked")} />}
      <List className="bm-settings-list" dataSource={blocked.data ?? []} renderItem={(b) => (
        <List.Item actions={[
          <Button key="u" type="link" onClick={() => void rulesApi.unblock(b.id).then(() => qc.invalidateQueries({ queryKey: rulesKeys.blocked }))}>
            {t("filters.unblock")}
          </Button>,
        ]}>
          {b.addressOrDomain}
        </List.Item>
      )} />
    </>
  );
}
