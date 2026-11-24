import { expect, test } from "@playwright/test";
import { expectNoHorizontalScroll, signIn, tag } from "./helpers";

test("create a Quick Step and run it on a conversation", async ({ page }) => {
  const name = `Đã xem ${tag()}`;
  await signIn(page, "hoa@bluemail.test");
  await page.locator(".bm-conv-main").first().click();
  await page.getByRole("button", { name: "Quick Steps" }).click();
  await page.getByRole("menuitem", { name: "Quản lý Quick Steps…" }).click();

  const dialog = page.getByRole("dialog", { name: "Quản lý Quick Steps…" });
  await dialog.getByRole("button", { name: /Quick Step mới/ }).click();
  await dialog.getByRole("textbox", { name: "Tên" }).last().fill(name);
  // The new step starts as "mark as read" + "archive"; make the second one "flag" so the conversation stays.
  await dialog.locator(".bm-quickstep-action").last().locator(".ant-select-selector").first().click();
  await page.locator(".ant-select-dropdown:visible .ant-select-item-option", { hasText: "Gắn cờ (sao)" }).click();
  await expectNoHorizontalScroll(page);
  await dialog.getByRole("button", { name: "Lưu" }).click();
  await expect(page.getByText("Đã lưu Quick Steps.")).toBeVisible();

  await page.getByRole("button", { name: "Quick Steps" }).click();
  await page.getByRole("menuitem", { name }).click();
  await expect(page.getByText(`Đã chạy “${name}”.`)).toBeVisible();
  await expect(page.locator("#bm-thread-subject").locator("xpath=..").getByRole("button", { name: "Bỏ dấu sao" })).toBeVisible();
});
