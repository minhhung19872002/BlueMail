import { expect, test } from "@playwright/test";
import { expectNoHorizontalScroll, signIn, tag } from "./helpers";

test.describe("administration", () => {
  test("add a domain and a user", async ({ page }) => {
    const t = tag();
    await signIn(page, "lan@bluemail.test", "/admin/domains");

    // A new domain appears unverified, with the DNS records to publish.
    const domain = `e2e-${t}.test`;
    await page.getByRole("textbox", { name: "Tên miền mới, ví dụ congty.vn" }).fill(domain);
    await page.getByRole("button", { name: /^(plus )?Thêm$/ }).click();
    // Its record table (MX, SPF, DKIM, DMARC… to publish) opens with it.
    await expect(page.getByRole("row").filter({ hasText: domain }).first()).toBeVisible();
    await expect(page.getByRole("cell", { name: "MX", exact: true }).first()).toBeVisible();
    await expectNoHorizontalScroll(page);
    await page.keyboard.press("Escape");

    // A user on the organisation's main domain.
    await page.goto("/admin/users");
    await page.getByRole("button", { name: /Thêm người dùng$/ }).click();
    const dialog = page.getByRole("dialog", { name: "Thêm người dùng" });
    await dialog.getByLabel("Địa chỉ thư").fill(`e2e-${t}@bluemail.test`);
    await dialog.getByLabel("Tên").fill(`Người dùng E2E ${t}`);
    await dialog.locator("input[type=password]").fill(`E2e-Password-${t}-2026`);
    await dialog.getByRole("button", { name: /^Tạo$/ }).click();
    await expect(page.getByText("Đã tạo người dùng.")).toBeVisible();
    await page.getByPlaceholder("Tìm theo tên hoặc địa chỉ").fill(`e2e-${t}`);
    await expect(page.getByText(`e2e-${t}@bluemail.test`)).toBeVisible();
    await expectNoHorizontalScroll(page);
  });
});
