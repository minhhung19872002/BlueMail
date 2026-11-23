import { useEffect, useMemo, useState } from "react";
import { Navigate, Route, Routes, useNavigate } from "react-router-dom";
import { App as AntApp, ConfigProvider, Result, Spin } from "antd";
import enUS from "antd/locale/en_US";
import viVN from "antd/locale/vi_VN";
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
import { SessionsPage } from "./modules/settings/SessionsPage";
import { UsersPage } from "./modules/admin/UsersPage";
import { TenantsPage } from "./modules/admin/TenantsPage";

const antdLocales = { en: enUS, vi: viVN } as const;

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
      <Routes>
        <Route element={<AppShell />}>
          <Route path="/" element={<Navigate to="/mail/inbox" replace />} />
          <Route path="/mail/label/:labelId" element={<MailboxPage />} />
          <Route path="/mail/:label" element={<MailboxPage />} />
          <Route path="/settings/sessions" element={<SessionsPage />} />
          <Route path="/admin/users" element={<UsersPage />} />
          <Route path="/admin/tenants" element={<TenantsPage />} />
          <Route path="*" element={<Navigate to="/mail/inbox" replace />} />
        </Route>
      </Routes>
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
        <Routes>
          <Route path="/auth/callback" element={<Callback />} />
          <Route path="*" element={<AuthGate />} />
        </Routes>
      </AntApp>
    </ConfigProvider>
  );
}
