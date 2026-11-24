import { useEffect, useMemo, useRef, useState } from "react";
import { useLocation, useNavigate, useSearchParams } from "react-router-dom";
import { AutoComplete, Button, Checkbox, Col, Grid, Input, Modal, Popover, Row, Select, Space } from "antd";
import { FilterOutlined, HistoryOutlined, SearchOutlined, TagOutlined, UserOutlined } from "@ant-design/icons";
import { useQuery } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { buildQuery, emptySearch, readRecent, rememberSearch, searchSuggestions, type AdvancedSearch, type SearchSuggestion, type Within } from "./search";
import { mailKeys } from "./mailApi";
import { api } from "../../api/client";
import type { Labels } from "../../api/types";
import { composeApi, type Suggestion } from "../compose/composeApi";

/** Search box with Gmail operators and an advanced form that writes the operators for you (PROMPT 6.7). */
export function SearchBox() {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const location = useLocation();
  const [params] = useSearchParams();
  const current = location.pathname === "/mail/search" ? params.get("q") ?? "" : "";
  const [text, setText] = useState(current);
  const [open, setOpen] = useState(false);
  const [form, setForm] = useState<AdvancedSearch>(emptySearch);
  // Phones: a dialog; a popover anchored to the box would run off the screen (cf. BUG-024).
  const narrow = Grid.useBreakpoint().md === false;
  useEffect(() => setText(current), [current]);

  // Suggestions while typing (PROMPT 6.7): recent searches, labels, contacts.
  const labels = useQuery({ queryKey: mailKeys.labels, queryFn: () => api.get<Labels>("/api/v1/mail/labels") });
  const mailboxId = labels.data?.mailboxId ?? "";
  const [recent, setRecent] = useState<string[]>([]);
  const [contacts, setContacts] = useState<Suggestion[]>([]);
  const timer = useRef<number>(undefined);
  useEffect(() => setRecent(mailboxId ? readRecent(mailboxId) : []), [mailboxId]);
  useEffect(() => () => window.clearTimeout(timer.current), []);
  const onType = (q: string) => {
    setText(q);
    window.clearTimeout(timer.current);
    if (q.trim().length < 2) {
      setContacts([]);
      return;
    }
    timer.current = window.setTimeout(() => {
      void composeApi.suggest(q.trim()).then(setContacts, () => setContacts([]));
    }, 200);
  };
  const suggestions = useMemo(
    () => searchSuggestions(text, { recent, labels: labels.data?.labels ?? [], contacts: contacts.filter((c) => c.kind !== "group") }),
    [text, recent, labels.data, contacts],
  );
  const icons: Record<SearchSuggestion["kind"], React.ReactNode> = { recent: <HistoryOutlined />, label: <TagOutlined />, contact: <UserOutlined /> };

  const go = (q: string) => {
    const query = q.trim();
    if (!query) return;
    setOpen(false);
    if (mailboxId) setRecent(rememberSearch(mailboxId, query));
    navigate(`/mail/search?q=${encodeURIComponent(query)}`);
  };
  const set = (patch: Partial<AdvancedSearch>) => setForm((f) => ({ ...f, ...patch }));
  const built = buildQuery(form);
  const within: Within[] = ["", "1d", "3d", "7d", "14d", "1m", "2m", "6m", "1y"];

  const advanced = (
    <div className="bm-advanced-search" role="form" aria-label={t("search.advanced")}>
      <Row gutter={[8, 8]}>
        {(["from", "to", "subject", "hasWords", "notWords"] as const).map((k) => (
          <Col span={24} key={k}>
            <Input addonBefore={t(`search.f.${k}`)} value={form[k]} onChange={(e) => set({ [k]: e.target.value })} onPressEnter={() => go(built)} />
          </Col>
        ))}
        <Col span={24}>
          <Space.Compact className="bm-full">
            <Select value={form.sizeOp} onChange={(v) => set({ sizeOp: v })} aria-label={t("search.f.size")}
              options={[{ value: "larger", label: t("search.larger") }, { value: "smaller", label: t("search.smaller") }]} />
            <Input inputMode="numeric" value={form.size} onChange={(e) => set({ size: e.target.value.replace(/\D/g, "") })} aria-label={t("search.f.size")} />
            <Select value={form.sizeUnit} onChange={(v) => set({ sizeUnit: v })} aria-label={t("search.unit")} options={[{ value: "K", label: "KB" }, { value: "M", label: "MB" }]} />
          </Space.Compact>
        </Col>
        <Col span={24}>
          <Space.Compact className="bm-full">
            <Select className="bm-within" value={form.within} onChange={(v) => set({ within: v })} aria-label={t("search.f.within")}
              options={within.map((w) => ({ value: w, label: t(w ? `search.within.${w}` : "search.within.any") }))} />
            <Input placeholder="yyyy/mm/dd" value={form.date} onChange={(e) => set({ date: e.target.value })} aria-label={t("search.f.date")} />
          </Space.Compact>
        </Col>
        <Col span={24}>
          <Select className="bm-full" value={form.where} onChange={(v) => set({ where: v })} aria-label={t("search.f.where")}
            options={["", "inbox", "starred", "sent", "drafts", "spam", "trash", "anywhere"].map((w) => ({ value: w, label: t(`search.where.${w || "all"}`) }))} />
        </Col>
        <Col span={24}>
          <Checkbox checked={form.hasAttachment} onChange={(e) => set({ hasAttachment: e.target.checked })}>{t("search.f.hasAttachment")}</Checkbox>
          <Checkbox checked={form.unread} onChange={(e) => set({ unread: e.target.checked })}>{t("search.f.unread")}</Checkbox>
        </Col>
        <Col span={24}><code className="bm-query" aria-live="polite">{built || "—"}</code></Col>
        <Col span={24}>
          <Space wrap>
            <Button disabled={!built} onClick={() => { setOpen(false); navigate(`/settings/filters?criteria=${encodeURIComponent(built)}`); }}>
              {t("search.createFilter")}
            </Button>
            <Button onClick={() => setForm(emptySearch)}>{t("search.clear")}</Button>
            <Button type="primary" icon={<SearchOutlined />} disabled={!built} onClick={() => go(built)}>{t("search.search")}</Button>
          </Space>
        </Col>
      </Row>
    </div>
  );

  return (
    <form role="search" className="bm-search" onSubmit={(e) => { e.preventDefault(); go(text); }}>
      <AutoComplete
        className="bm-full"
        value={text}
        onSearch={onType}
        onChange={(v) => setText(v)}
        onSelect={(_, o) => { const q = (o as { query: string }).query; setText(q); go(q); }}
        options={suggestions.map((s) => ({
          value: s.query,
          query: s.query,
          label: (
            <span className="bm-suggest" title={t(`search.suggest.${s.kind}`)}>
              <span aria-hidden>{icons[s.kind]}</span> <span className="bm-visually-hidden">{t(`search.suggest.${s.kind}`)}: </span>{s.label}
            </span>
          ),
        }))}
      >
      <Input
        placeholder={t("search.placeholder")}
        aria-label={t("search.placeholder")}
        maxLength={2000}
        prefix={<SearchOutlined aria-hidden />}
        allowClear
        suffix={narrow ? (
          <Button type="text" size="small" icon={<FilterOutlined />} aria-label={t("search.advanced")} aria-expanded={open} onClick={() => setOpen(true)} />
        ) : (
          <Popover open={open} onOpenChange={setOpen} trigger="click" placement="bottomRight" content={advanced} destroyOnHidden>
            <Button type="text" size="small" icon={<FilterOutlined />} aria-label={t("search.advanced")} aria-expanded={open} />
          </Popover>
        )}
      />
      </AutoComplete>
      {narrow && (
        <Modal open={open} title={t("search.advanced")} footer={null} onCancel={() => setOpen(false)} destroyOnHidden>
          {advanced}
        </Modal>
      )}
    </form>
  );
}
