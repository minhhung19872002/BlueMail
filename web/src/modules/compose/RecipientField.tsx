import { useEffect, useRef, useState } from "react";
import { AutoComplete, Tag, Tooltip } from "antd";
import { TeamOutlined, InboxOutlined, UserOutlined, HistoryOutlined } from "@ant-design/icons";
import { useTranslation } from "react-i18next";
import { composeApi, type Suggestion } from "./composeApi";
import { domainOf, isValidAddress, parseRecipients, type Recipient } from "./compose";

export const recipientDragType = "application/x-bluemail-recipient";

interface Props {
  label: string;
  field: "to" | "cc" | "bcc";
  value: Recipient[];
  onChange: (v: Recipient[]) => void;
  ourDomains: string[];
  autoFocus?: boolean;
  /** A chip dragged in from another field: remove it there. */
  onMovedIn: (r: Recipient, from: "to" | "cc" | "bcc") => void;
}

const kindIcon = { user: <UserOutlined />, group: <TeamOutlined />, shared: <InboxOutlined />, recent: <HistoryOutlined /> } as const;

/** To / Cc / Bcc (PROMPT 6.4): suggestions, paste a list, external recipients marked, drag chips between fields. */
export function RecipientField({ label, field, value, onChange, ourDomains, autoFocus, onMovedIn }: Props) {
  const { t } = useTranslation();
  const [text, setText] = useState("");
  const [options, setOptions] = useState<Suggestion[]>([]);
  const timer = useRef<number | undefined>(undefined);
  const ours = ourDomains.map((d) => d.toLowerCase());

  useEffect(() => () => window.clearTimeout(timer.current), []);

  const add = (raw: string) => {
    const { valid, invalid } = parseRecipients(raw);
    const known = new Set(value.map((r) => r.address.toLowerCase()));
    const fresh = valid.filter((r) => !known.has(r.address.toLowerCase()));
    if (fresh.length) onChange([...value, ...fresh]);
    setText(invalid.join(", "));
  };

  const search = (q: string) => {
    setText(q);
    window.clearTimeout(timer.current);
    if (q.trim().length < 2) {
      setOptions([]);
      return;
    }
    timer.current = window.setTimeout(() => {
      void composeApi.suggest(q).then(setOptions, () => setOptions([]));
    }, 200);
  };

  return (
    <div
      className="bm-rcpt-field"
      onDragOver={(e) => {
        if (e.dataTransfer.types.includes(recipientDragType)) e.preventDefault();
      }}
      onDrop={(e) => {
        const raw = e.dataTransfer.getData(recipientDragType);
        if (!raw) return;
        e.preventDefault();
        const { r, from } = JSON.parse(raw) as { r: Recipient; from: "to" | "cc" | "bcc" };
        if (from === field) return;
        if (!value.some((x) => x.address.toLowerCase() === r.address.toLowerCase())) onChange([...value, r]);
        onMovedIn(r, from);
      }}
    >
      <span className="bm-rcpt-label">{label}</span>
      <div
        className="bm-rcpt-chips"
        onPasteCapture={(e) => {
          // A pasted list ("a@x, B <b@y>; c@z") becomes several chips at once.
          const pasted = e.clipboardData.getData("text");
          if (/[,;\n]/.test(pasted)) {
            e.preventDefault();
            add(text + pasted);
          }
        }}
      >
        {value.map((r) => {
          const external = !ours.includes(domainOf(r.address));
          return (
            <Tooltip key={r.address} title={r.address}>
              <Tag
                closable
                draggable
                onDragStart={(e) => e.dataTransfer.setData(recipientDragType, JSON.stringify({ r, from: field }))}
                className={external ? "bm-rcpt-external" : undefined}
                onClose={() => onChange(value.filter((x) => x !== r))}
                closeIcon={<span aria-label={t("compose.removeRecipient", { address: r.address })}>×</span>}
              >
                {r.name || r.address}
              </Tag>
            </Tooltip>
          );
        })}
        <AutoComplete
          className="bm-rcpt-input"
          defaultActiveFirstOption
          variant="borderless"
          value={text}
          autoFocus={autoFocus}
          options={options.map((o) => ({
            value: o.address,
            label: (
              <span className="bm-suggest">
                {kindIcon[o.kind]} <span className="bm-strong">{o.name || o.address}</span> {o.name && <span className="bm-muted">{o.address}</span>}
              </span>
            ),
            suggestion: o,
          }))}
          onSearch={search}
          onSelect={(_, opt) => {
            const s = (opt as { suggestion: Suggestion }).suggestion;
            add(s.name ? `"${s.name.replace(/"/g, "")}" <${s.address}>` : s.address);
            setText("");
            setOptions([]);
          }}
          onInputKeyDown={(e) => {
            if ((e.key === "Enter" || e.key === "," || e.key === ";" || e.key === "Tab") && text.trim() && options.length === 0) {
              if (e.key !== "Tab") e.preventDefault();
              add(text);
            } else if (e.key === "Backspace" && !text && value.length > 0) {
              onChange(value.slice(0, -1));
            }
          }}
          onBlur={() => text.trim() && isValidAddress(text.trim()) && add(text)}
          aria-label={label}
        />
      </div>
      {text && !isValidAddress(text.trim()) && /[,;@]/.test(text) && (
        <span className="bm-rcpt-invalid" role="alert">{t("compose.invalidAddress", { text })}</span>
      )}
    </div>
  );
}
