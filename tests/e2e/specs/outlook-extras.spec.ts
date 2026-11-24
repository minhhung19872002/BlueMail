import { expect, test } from "@playwright/test";
import { expectNoHorizontalScroll, signIn, tag } from "./helpers";

// Outlook features added after the first e2e run (docs/11): list sort, assign task.

test("the inbox can be sorted by sender and the order is kept in the URL", async ({ page }) => {
  await signIn(page, "minh@bluemail.test");
  await page.getByRole("button", { name: /^Sắp xếp:/ }).click();
  const answer = page.waitForResponse((r) => r.url().includes("/api/v1/mail/conversations") && r.url().includes("sort=from"));
  await page.getByRole("menuitem", { name: "Người gửi (A đến Z)" }).click();
  expect((await answer).ok()).toBeTruthy();
  await expect(page).toHaveURL(/[?&]s=from-asc/);
  await expect(page.getByRole("button", { name: "Sắp xếp: Người gửi (A đến Z)" })).toBeVisible();
  await expectNoHorizontalScroll(page);

  await page.reload();
  await expect(page.getByRole("button", { name: "Sắp xếp: Người gửi (A đến Z)" })).toBeVisible();
});

test("an assigned task completes for the assigner when the assignee completes it", async ({ page, browser }) => {
  const title = `Giao ${tag()}`;
  await signIn(page, "hoa@bluemail.test", "/tasks");
  const box = page.getByRole("textbox", { name: "Thêm việc" });
  await box.fill(title);
  await box.press("Enter");
  await page.getByText(title).click();
  await page.getByRole("button", { name: "Giao việc" }).click();
  const dialog = page.getByRole("dialog", { name: "Giao việc" });
  await dialog.getByRole("textbox", { name: "Địa chỉ email đồng nghiệp" }).fill("minh@bluemail.test");
  const assigned = page.waitForResponse((r) => r.url().includes("/assign") && r.request().method() === "POST");
  await dialog.getByRole("button", { name: /OK|Đồng ý/ }).click();
  expect((await assigned).ok()).toBeTruthy();
  await expect(page.getByText(/^Đã giao cho /).first()).toBeVisible();
  await expectNoHorizontalScroll(page);

  const other = await browser.newPage({ viewport: page.viewportSize() ?? undefined });
  await signIn(other, "minh@bluemail.test", "/tasks");
  await expect(other.getByText(title)).toBeVisible();
  const done = other.waitForResponse((r) => r.url().includes("/api/v1/tasks/") && r.request().method() === "PUT");
  await other.getByRole("checkbox", { name: `Hoàn thành: ${title}` }).click();
  expect((await done).ok()).toBeTruthy();
  await other.close();

  await page.getByText("Đã xong", { exact: true }).click();
  await expect(page.getByText(title)).toBeVisible();
});

test("filter To me, the Schedule view and E-mail calendar", async ({ page }) => {
  await signIn(page, "minh@bluemail.test");
  await page.locator(".bm-list-filter").click();
  const filtered = page.waitForResponse((r) => r.url().includes("/api/v1/mail/conversations") && decodeURIComponent(r.url()).includes("to:me"));
  await page.getByTitle("Gửi cho tôi").click();
  expect((await filtered).ok()).toBeTruthy();
  await expect(page).toHaveURL(/[?&]f=toMe/);

  await page.goto("/calendar");
  await page.getByText("Lịch biểu", { exact: true }).click();
  await expect(page.locator(".bm-cal-agenda > li")).toHaveCount(7);
  await expectNoHorizontalScroll(page);

  await page.getByRole("button", { name: "Gửi lịch qua email" }).click();
  const ics = page.waitForResponse((r) => r.url().includes("/api/v1/calendar/export.ics"));
  await page.getByRole("button", { name: "Tạo thư" }).click();
  expect((await ics).ok()).toBeTruthy();
  await expect(page.getByText(/^calendar\.ics/)).toBeVisible({ timeout: 20_000 });
});

test("a contact can be shared, and a meeting started from a contact", async ({ page }) => {
  const name = `Đối tác ${tag()}`;
  await signIn(page, "hoa@bluemail.test", "/contacts");
  await page.getByRole("button", { name: "Liên hệ mới" }).click();
  await page.getByRole("dialog").getByRole("textbox").first().fill(name);
  await page.getByRole("dialog").locator("input[type=email]").first().fill(`${tag()}@doitac.test`);
  await page.getByRole("dialog").getByRole("button", { name: "Lưu" }).click();
  await expect(page.getByText(name)).toBeVisible();

  await page.getByRole("button", { name: "Chia sẻ danh bạ" }).click();
  const dialog = page.getByRole("dialog", { name: "Chia sẻ danh bạ của tôi" });
  await dialog.getByRole("textbox", { name: "Địa chỉ email đồng nghiệp" }).fill("minh@bluemail.test");
  const shared = page.waitForResponse((r) => r.url().endsWith("/api/v1/contacts/shares") && r.request().method() === "PUT");
  await dialog.getByRole("button", { name: "Chia sẻ", exact: true }).click();
  expect((await shared).ok()).toBeTruthy();
  await expect(dialog.getByText("minh@bluemail.test")).toBeVisible();
  await dialog.getByRole("button", { name: "Close" }).click().catch(() => page.keyboard.press("Escape"));

  await page.getByRole("button", { name: `Họp với ${name}` }).click();
  await expect(page).toHaveURL(/\/calendar/);
  await expect(page.getByRole("dialog", { name: "Sự kiện mới" })).toBeVisible();
});
