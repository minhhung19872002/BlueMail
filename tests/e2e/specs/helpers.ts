import { expect, type Page } from "@playwright/test";

/** Dev sample data (deploy/.env.dev, not secret): every sample user has this password; lan@ administers bluemail.test. */
export const Password = process.env.BM_E2E_PASSWORD ?? "Sample-User-Dev-1";

/** Signs in through BlueID (OIDC code + PKCE) and waits for the mailbox. */
export async function signIn(page: Page, email: string, path = "/mail/inbox"): Promise<void> {
  await page.goto(path);
  // BlueID allows 20 sign-ins a minute per address; a long run can hit it: wait for the window and retry once.
  if (await page.getByText("rate_limited").isVisible().catch(() => false)) {
    await page.waitForTimeout(61_000);
    await page.reload();
  }
  await page.locator("#email").fill(email);
  await page.locator("#password").fill(Password);
  await page.locator("form button[type=submit]").click();
  await page.waitForURL((u) => u.port === "5173" && !u.pathname.startsWith("/auth/"), { timeout: 30_000 });
  await expect(page.locator("main")).toBeVisible();
}

/** PROMPT 11: no page may scroll sideways, at either screen size. */
export async function expectNoHorizontalScroll(page: Page): Promise<void> {
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
  expect(overflow, "horizontal overflow in px").toBeLessThanOrEqual(1);
}

export const isPhone = (page: Page) => (page.viewportSize()?.width ?? 1366) < 768;

/** A short unique tag so repeated runs do not see each other's data. */
export const tag = () => Math.random().toString(36).slice(2, 8);
