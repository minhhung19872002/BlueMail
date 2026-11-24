import { expect, test } from "@playwright/test";
import { expectNoHorizontalScroll, isPhone, signIn, tag } from "./helpers";

test.describe("mail", () => {
  test("sign in, read a conversation", async ({ page }) => {
    await signIn(page, "minh@bluemail.test");
    await expect(page.getByRole("heading", { name: "Hộp thư đến", level: 4 })).toBeVisible();
    await expectNoHorizontalScroll(page);

    const first = page.locator(".bm-conv-main").first();
    await first.click();
    await expect(page.locator("#bm-thread-subject")).toBeVisible();
    await expect(page.locator(".bm-msg").first()).toBeVisible();
    await expectNoHorizontalScroll(page);
  });

  test("compose with an attachment, undo the send, send again", async ({ page }) => {
    const t = tag();
    const subject = `E2E ${t} tệp đính kèm`;
    await signIn(page, "minh@bluemail.test");
    // On phones the folder pane is closed and Compose is the floating button (the last one in the page).
    const composeButtons = page.getByRole("button", { name: "Soạn thư" });
    await (isPhone(page) ? composeButtons.last() : composeButtons.first()).click();

    const to = page.getByRole("combobox", { name: "Tới" });
    await to.fill("hoa@bluemail.test");
    await to.press("Enter");
    await page.getByPlaceholder("Tiêu đề").fill(subject);
    await page.locator(".ProseMirror").first().fill("Nội dung thử e2e " + t);

    const chooser = page.waitForEvent("filechooser");
    await page.getByRole("button", { name: "Đính kèm tệp" }).click();
    await (await chooser).setFiles({ name: `bao-cao-${t}.txt`, mimeType: "text/plain", buffer: Buffer.from("Báo cáo " + t, "utf8") });
    await expect(page.getByText(`bao-cao-${t}.txt`)).toBeVisible();
    // Sending waits for uploads to finish (the window says so); wait until the upload progress is gone.
    await expect(page.locator(".ant-modal, .bm-compose").getByRole("progressbar")).toHaveCount(0);
    await expect(page.getByText("Đã lưu")).toBeVisible();
    await expectNoHorizontalScroll(page);

    // Send, then undo inside the window: the draft opens again.
    await page.getByRole("button", { name: "Gửi", exact: true }).click();
    await page.getByRole("button", { name: "Hoàn tác" }).click();
    await expect(page.getByPlaceholder("Tiêu đề")).toHaveValue(subject);

    // Wait for the send request itself: the first send's notice may still be fading out.
    const sent = page.waitForResponse((r) => /\/drafts\/[^/]+\/send$/.test(new URL(r.url()).pathname) && r.request().method() === "POST");
    await page.getByRole("button", { name: "Gửi", exact: true }).click();
    expect((await sent).status()).toBe(200);

    // It arrives in Sent once the undo window has passed and the dispatcher ran: reload until it shows.
    await expect(async () => {
      await page.goto("/mail/sent");
      await expect(page.locator(".bm-conv-main", { hasText: subject })).toHaveCount(1, { timeout: 3_000 });
    }).toPass({ timeout: 60_000, intervals: [2_000] });
  });

  test("search with an operator, create a filter from it, snooze", async ({ page }) => {
    await signIn(page, "minh@bluemail.test");
    const box = page.getByRole("combobox", { name: "Tìm thư" });
    await box.fill("from:an@doitac.test");
    await box.press("Enter");
    await expect(page).toHaveURL(/q=from%3Aan%40doitac\.test|q=from:an@doitac\.test/);
    await expect(page.locator(".bm-conv-main").first()).toBeVisible();
    await expectNoHorizontalScroll(page);

    // Filter from the advanced search form (Gmail-style "Create filter").
    await page.getByRole("button", { name: "Tìm kiếm nâng cao" }).click();
    const form = page.getByRole("form", { name: "Tìm kiếm nâng cao" });
    await form.getByRole("textbox").first().fill("an@doitac.test");
    await form.getByRole("button", { name: "Tạo bộ lọc" }).click();
    await expect(page).toHaveURL(/\/settings\/filters/);
    await expectNoHorizontalScroll(page);

    // Snooze the first conversation until tomorrow.
    await page.goto("/mail/inbox");
    await page.locator(".bm-conv-main").first().click();
    await page.getByRole("button", { name: /Tạm ẩn/ }).first().click();
    await page.getByText("Ngày mai", { exact: false }).first().click();
    await expect(page.getByText(/Đã tạm ẩn/)).toBeVisible();
    if (!isPhone(page)) await expectNoHorizontalScroll(page);
  });

  test("calendar and to do pages fit the screen", async ({ page }) => {
    await signIn(page, "minh@bluemail.test", "/calendar");
    await expect(page.getByRole("button", { name: "Sự kiện mới" })).toBeVisible();
    await expectNoHorizontalScroll(page);
    await page.goto("/tasks");
    await expect(page.getByRole("heading", { name: "Việc cần làm" })).toBeVisible();
    await expectNoHorizontalScroll(page);
    await page.goto("/contacts");
    await expectNoHorizontalScroll(page);
    await page.goto("/settings");
    await expectNoHorizontalScroll(page);
  });
});
