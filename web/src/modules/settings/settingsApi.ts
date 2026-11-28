import { useEffect } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { api } from "../../api/client";
import { useUi, type Density, type ReadingPane, type ThemePreference } from "../../store/ui";

// Account settings stored on the server (mail.user_settings, PROMPT 6.11): they follow the user to every device.

export type Category = "PRIMARY" | "PROMOTIONS" | "SOCIAL" | "UPDATES" | "FORUMS";

export interface MailSettings {
  pageSize: 25 | 50 | 100;
  density: "DEFAULT" | "COMFORTABLE" | "COMPACT";
  readingPane: "NONE" | "RIGHT" | "BELOW";
  conversationView: boolean;
  undoSendSeconds: 5 | 10 | 20 | 30;
  defaultReply: "REPLY" | "REPLY_ALL";
  externalImages: "ASK" | "ALWAYS";
  keyboardShortcuts: boolean;
  inboxCategories: Category[];
  theme: "SYSTEM" | "LIGHT" | "DARK";
  sendAndArchive: boolean;
  autoAdvance: "LIST" | "NEWER" | "OLDER";
  desktopNotifications: boolean;
  /** Outlook "Read receipts" for mail that asks for one. */
  readReceipts: "ASK" | "ALWAYS" | "NEVER";
  /** Lists pinned at the top of the folder pane (paths such as /mail/inbox). */
  favorites: string[];
  /** Outlook "Quick Steps": named sequences of mail actions. */
  quickSteps: QuickStep[];
  /** Outlook "Calendar groups": named sets of shared calendars (user ids). */
  calendarGroups: CalendarGroup[];
}

export interface CalendarGroup {
  id: string;
  name: string;
  members: string[];
}

export interface QuickStepAction {
  action: string;
  labelId: string | null;
}

export interface QuickStep {
  id: string;
  name: string;
  actions: QuickStepAction[];
}

export interface Profile {
  displayName: string;
  locale: string | null;
  timeZone: string | null;
  hourCycle: "h12" | "h23" | null;
  weekStart: number | null;
}

export const settingsKey = ["settings"] as const;

export const settingsApi = {
  get: () => api.get<MailSettings>("/api/v1/me/settings"),
  save: (s: MailSettings) => api.put<MailSettings>("/api/v1/me/settings", s),
  saveProfile: (p: Profile) => api.put<boolean>("/api/v1/me/profile", p),
};

/** Server settings, mirrored into the device store the layout already reads (theme, density, pane, undo). */
export function useSettings(): MailSettings | undefined {
  const q = useQuery({ queryKey: settingsKey, queryFn: settingsApi.get, staleTime: 60_000 });
  const ui = useUi();
  useEffect(() => {
    const s = q.data;
    if (!s) return;
    const theme = s.theme.toLowerCase() as ThemePreference;
    const density = s.density.toLowerCase() as Density;
    const pane = s.readingPane.toLowerCase() as ReadingPane;
    if (ui.theme !== theme) ui.setTheme(theme);
    if (ui.density !== density) ui.setDensity(density);
    if (ui.readingPane !== pane) ui.setReadingPane(pane);
    if (ui.undoSeconds !== s.undoSendSeconds) ui.setUndoSeconds(s.undoSendSeconds);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [q.data]);
  return q.data;
}

/** Saves a change and refreshes everything that depends on it. */
export function useSaveSettings() {
  const qc = useQueryClient();
  return async (patch: Partial<MailSettings>) => {
    const current = qc.getQueryData<MailSettings>(settingsKey) ?? (await settingsApi.get());
    const saved = await settingsApi.save({ ...current, ...patch });
    qc.setQueryData(settingsKey, saved);
    return saved;
  };
}
