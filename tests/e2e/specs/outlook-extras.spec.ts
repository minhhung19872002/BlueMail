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
  await other.getByRole("checkbox", { name: `Hoàn thành: ${title}` }).check();
  expect((await done).ok()).toBeTruthy();
  await other.close();

  await page.getByText("Đã xong", { exact: true }).click();
  await expect(page.getByText(title)).toBeVisible();
});
