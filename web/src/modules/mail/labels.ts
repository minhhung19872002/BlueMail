import type { LabelSummary } from "../../api/types";

// System views of the left column, in Gmail order. "all" is virtual: every message not in SPAM/TRASH.
export interface SystemView {
  route: string;
  systemKey: string | null;
  i18nKey: string;
  countUnread: boolean;
  countTotal: boolean;
}

export const systemViews: SystemView[] = [
  { route: "inbox", systemKey: "INBOX", i18nKey: "labels.inbox", countUnread: true, countTotal: false },
  { route: "starred", systemKey: "STARRED", i18nKey: "labels.starred", countUnread: false, countTotal: false },
  { route: "snoozed", systemKey: "SNOOZED", i18nKey: "labels.snoozed", countUnread: false, countTotal: false },
  { route: "important", systemKey: "IMPORTANT", i18nKey: "labels.important", countUnread: false, countTotal: false },
  { route: "sent", systemKey: "SENT", i18nKey: "labels.sent", countUnread: false, countTotal: false },
  { route: "drafts", systemKey: "DRAFTS", i18nKey: "labels.drafts", countUnread: false, countTotal: true },
  { route: "scheduled", systemKey: "SCHEDULED", i18nKey: "labels.scheduled", countUnread: false, countTotal: true },
  { route: "all", systemKey: null, i18nKey: "labels.all", countUnread: false, countTotal: false },
  { route: "spam", systemKey: "SPAM", i18nKey: "labels.spam", countUnread: true, countTotal: false },
  { route: "trash", systemKey: "TRASH", i18nKey: "labels.trash", countUnread: false, countTotal: false },
];

export function countFor(view: SystemView, labels: LabelSummary[]): number {
  const l = labels.find((x) => x.systemKey === view.systemKey);
  if (!l) return 0;
  return view.countTotal ? l.total : view.countUnread ? l.unread : 0;
}

export function userLabels(labels: LabelSummary[]): LabelSummary[] {
  return labels.filter((l) => l.systemKey === null && l.showInList !== "HIDE");
}

/** Background colours offered for user labels (white text on each meets WCAG AA 4.5:1). */
export const labelPalette = ["#1a73e8", "#188038", "#c5221f", "#b06000", "#7627bb", "#007b83", "#5f6368", "#b80672"] as const;

/** Route of a list: system views by name, user labels by id. */
export function routeOf(label: LabelSummary): string {
  const v = systemViews.find((x) => x.systemKey === label.systemKey && label.systemKey !== null);
  return v ? `/mail/${v.route}` : `/mail/label/${label.id}`;
}
