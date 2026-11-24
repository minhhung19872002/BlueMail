import { expect, test, type Page } from "@playwright/test";
import { expectNoHorizontalScroll, signIn, tag } from "./helpers";

/** Opens "New event", sets the title and the start (YYYY-MM-DD HH:mm, user's zone); the end defaults to 30 minutes later. */
async function newEvent(page: Page, title: string, start: string) {
  await page.getByRole("button", { name: "Sự kiện mới" }).click();
  const dialog = page.getByRole("dialog", { name: "Sự kiện mới" });
  await dialog.getByRole("textbox", { name: "Tiêu đề" }).fill(title);
  const startBox = dialog.getByRole("textbox", { name: "Bắt đầu" });
  await startBox.click();
  await startBox.fill(start);
  await startBox.press("Enter");
  return dialog;
}

test("scheduling assistant shows a colleague busy at the chosen time", async ({ page, browser }) => {
  const t = tag();
  // hoa is busy 10:00-10:30 on 2027-01-14.
  await signIn(page, "hoa@bluemail.test", "/calendar");
  const busyDialog = await newEvent(page, `Bận ${t}`, "2027-01-14 10:00");
  await busyDialog.getByRole("button", { name: "Lưu" }).click();
  await expect(page.getByText("Đã lưu sự kiện.")).toBeVisible();

  // minh plans a meeting at 10:15 with hoa: the assistant says she is busy; at 11:00 she is free.
  const other = await browser.newPage({ viewport: page.viewportSize() ?? undefined });
  await signIn(other, "minh@bluemail.test", "/calendar");
  const dialog = await newEvent(other, `Họp ${t}`, "2027-01-14 10:15");
  const guests = dialog.getByRole("combobox", { name: "Khách mời" });
  await guests.fill("hoa@bluemail.test");
  await guests.press("Enter");
  await expect(dialog.getByText("hoa@bluemail.test: bận giờ này")).toBeVisible();
  await expectNoHorizontalScroll(other);

  const startBox = dialog.getByRole("textbox", { name: "Bắt đầu" });
  await startBox.click();
  await startBox.fill("2027-01-14 11:00");
  await startBox.press("Enter");
  await expect(dialog.getByText("hoa@bluemail.test: rảnh")).toBeVisible();
  await other.close();
});
