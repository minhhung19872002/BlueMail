import { defineConfig } from "@playwright/test";

// Runs against the dev stack (scripts/dev-run.sh identity + api, vite on 5173, compose for mail).
// Uses the installed Chrome (channel "chrome"), so no browser download is needed; BM_E2E_BROWSER=chromium for CI.
const channel = process.env.BM_E2E_BROWSER === "chromium" ? undefined : "chrome";

export default defineConfig({
  testDir: "./specs",
  timeout: 90_000,
  expect: { timeout: 15_000 },
  fullyParallel: false,
  workers: 1,
  retries: 0,
  reporter: [["list"], ["html", { outputFolder: "out/report", open: "never" }]],
  outputDir: "out/results",
  use: {
    baseURL: process.env.BM_WEB_URL ?? "http://localhost:5173",
    channel,
    locale: "vi-VN",
    timezoneId: "Asia/Ho_Chi_Minh",
    trace: "retain-on-failure",
    screenshot: "only-on-failure",
  },
  projects: [
    { name: "desktop", use: { viewport: { width: 1366, height: 768 } } },
    { name: "phone", use: { viewport: { width: 375, height: 812 }, isMobile: true, hasTouch: true } },
  ],
});
