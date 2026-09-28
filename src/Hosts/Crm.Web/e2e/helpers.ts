import type { Page } from '@playwright/test';

/** Fills the login form by label and waits for the redirect to /segments. */
export async function login(page: Page, email: string, password: string): Promise<void> {
  await page.goto('/login');
  await page.getByLabel('Email').fill(email);
  await page.getByLabel('Password').fill(password);
  await page.getByRole('button', { name: /Sign in/ }).click();
  await page.waitForURL('**/segments');
}
