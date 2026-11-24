import { lazy, Suspense, useEffect, useMemo, useState } from "react";
import { Navigate, Route, Routes, useNavigate } from "react-router-dom";
import { App as AntApp, ConfigProvider, Result, Spin } from "antd";
import enUS from "antd/locale/en_US";
import viVN from "antd/locale/vi_VN";
import type { Locale } from "antd/es/locale";
import { useQuery } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { api } from "./api/client";
import type { Me } from "./api/types";
import { completeSignIn, currentUser, signIn } from "./auth/auth";
import { directionOf, pickLanguage } from "./i18n";
import { antdTheme, applyCssVariables, type Mode } from "./theme/tokens";
import { useUi } from "./store/ui";
import { MeContext } from "./session";
import { AppShell } from "./layout/AppShell";
import { MailboxPage } from "./modules/mail/MailboxPage";

// Pages other than the mailbox load on demand (smaller first download, PROMPT 8.2).
const SessionsPage = lazy(() => import("./modules/settings/SessionsPage").then((m) => ({ default: m.SessionsPage })));
const SignaturesPage = lazy(() => import("./modules/settings/SignaturesPage").then((m) => ({ default: m.SignaturesPage })));
const FiltersPage = lazy(() => import("./modules/settings/FiltersPage").then((m) => ({ default: m.FiltersPage })));
const VacationPage = lazy(() => import("./modules/settings/VacationPage").then((m) => ({ default: m.VacationPage })));
const ForwardingPage = lazy(() => import("./modules/settings/ForwardingPage").then((m) => ({ default: m.ForwardingPage })));
const CalendarPage = lazy(() => import("./modules/calendar/CalendarPage").then((m) => ({ default: m.CalendarPage })));
const ContactsPage = lazy(() => import("./modules/contacts/ContactsPage").then((m) => ({ default: m.ContactsPage })));
const SettingsPage = lazy(() => import("./modules/settings/SettingsPage").then((m) => ({ default: m.SettingsPage })));
const AdminConsole = lazy(() => import("./modules/admin/AdminConsole").then((m) => ({ default: m.AdminConsole })));
const ConfidentialPage = lazy(() => import("./modules/mail/ConfidentialPage").then((m) => ({ default: m.ConfidentialPage })));

// Vite may hand over the CommonJS build of antd/locale wrapped in { default } (seen: dialogs in English for vi).
const unwrap = (m: Locale): Locale => (m as Locale & { default?: Locale }).default ?? m;
const antdLocales = { en: unwrap(enUS), vi: unwrap(viVN) } as const;

function useSystemDark(): boolean {
  const query = "(prefers-color-scheme: dark)";
  const [dark, setDark] = useState(() => window.matchMedia(query).matches);
  useEffect(() => {
    const mq = window.matchMedia(query);
    const on = (e: MediaQueryListEvent) => setDark(e.matches);
    mq.addEventListener("change", on);
    return () => mq.removeEventListener("change", on);
  }, []);
  return dark;
}

function Callback() {
  const navigate = useNavigate();
  const { t } = useTranslation();
  const [failed, setFailed] = useState(false);
  useEffect(() => {
    completeSignIn().then((to) => navigate(to, { replace: true }), () => setFailed(true));
  }, [navigate]);
  return failed ? <Result status="error" title={t("auth.callbackFailed")} /> : <Spin fullscreen />;
}

function Authenticated() {
  const { i18n } = useTranslation();
  const me = useQuery({ queryKey: ["me"], queryFn: () => api.get<Me>("/api/v1/me") });

  useEffect(() => {
    if (me.data) void i18n.changeLanguage(pickLanguage([me.data.locale]));
  }, [me.data, i18n]);

  if (me.isPending) return <Spin fullscreen />;
  if (me.isError || !me.data) return <Result status="error" title={i18n.t("errors.loadProfile")} />;

  return (
    <MeContext.Provider value={me.data}>
      <Suspense fallback={<Spin fullscreen />}>
      <Routes>
        <Route element={<AppShell />}>
          <Route path="/" element={<Navigate to="/mail/inbox" replace />} />
          <Route path="/mail/label/:labelId" element={<MailboxPage />} />
          <Route path="/mail/:label" element={<MailboxPage />} />
          <Route path="/settings/sessions" element={<SessionsPage />} />
          <Route path="/settings/signatures" element={<SignaturesPage />} />
          <Route path="/settings/filters" element={<FiltersPage />} />
          <Route path="/settings/vacation" element={<VacationPage />} />
          <Route path="/settings/forwarding" element={<ForwardingPage />} />
          <Route path="/settings" element={<SettingsPage />} />
          <Route path="/contacts" element={<ContactsPage />} />
          <Route path="/calendar" element={<CalendarPage />} />
          <Route path="/admin" element={<AdminConsole />} />
          <Route path="/admin/:section" element={<AdminConsole />} />
          <Route path="*" element={<Navigate to="/mail/inbox" replace />} />
        </Route>
      </Routes>
      </Suspense>
    </MeContext.Provider>
  );
}

function AuthGate() {
  const { i18n } = useTranslation();
  const [state, setState] = useState<"checking" | "in" | "redirecting" | "failed">("checking");
  useEffect(() => {
    currentUser()
      .then((u) => {
        if (u) {
          setState("in");
          return;
        }
        setState("redirecting");
        return signIn(i18n.language);
      })
      .catch(() => setState("failed"));
  }, [i18n.language]);
  if (state === "failed") return <Result status="error" title={i18n.t("auth.unreachable")} />;
  return state === "in" ? <Authenticated /> : <Spin fullscreen />;
}

export function App() {
  const { i18n } = useTranslation();
  const preference = useUi((s) => s.theme);
  const density = useUi((s) => s.density);
  const systemDark = useSystemDark();
  const mode: Mode = preference === "system" ? (systemDark ? "dark" : "light") : preference;
  const lang = i18n.language as keyof typeof antdLocales;
  const dir = directionOf(i18n.language);

  useEffect(() => applyCssVariables(mode), [mode]);
  useEffect(() => {
    document.documentElement.lang = i18n.language;
    document.documentElement.dir = dir;
  }, [i18n.language, dir]);

  const themeConfig = useMemo(() => antdTheme(mode, density), [mode, density]);

  return (
    <ConfigProvider theme={themeConfig} locale={antdLocales[lang] ?? enUS} direction={dir}>
      <AntApp>
        <Suspense fallback={<Spin fullscreen />}>
        <Routes>
          <Route path="/auth/callback" element={<Callback />} />
          <Route path="/c/:token" element={<ConfidentialPage />} />
          <Route path="*" element={<AuthGate />} />
        </Routes>
        </Suspense>
      </AntApp>
    </ConfigProvider>
  );
}
