import { UserManager, WebStorageStateStore, type User } from "oidc-client-ts";
import { config } from "../config";

// Sign-in through BlueID with authorization code + PKCE (oidc-client-ts). Tokens live in sessionStorage,
// never in a cookie readable by mail content; the API is called with the bearer access token.
let manager: UserManager | null = null;

function userManager(): UserManager {
  if (!manager) {
    const origin = window.location.origin;
    manager = new UserManager({
      authority: config().identityUrl,
      client_id: "bluemail-web",
      redirect_uri: `${origin}/auth/callback`,
      post_logout_redirect_uri: `${origin}/`,
      response_type: "code",
      scope: "openid email profile offline_access bluemail-api",
      automaticSilentRenew: true,
      userStore: new WebStorageStateStore({ store: window.sessionStorage }),
    });
  }
  return manager;
}

export async function currentUser(): Promise<User | null> {
  const user = await userManager().getUser();
  return user && !user.expired ? user : null;
}

export async function accessToken(): Promise<string | null> {
  const user = await userManager().getUser();
  if (!user) return null;
  if (user.expired) {
    try {
      const renewed = await userManager().signinSilent();
      return renewed?.access_token ?? null;
    } catch {
      return null;
    }
  }
  return user.access_token;
}

export function signIn(uiLocale?: string): Promise<void> {
  const returnTo = window.location.pathname + window.location.search;
  return userManager().signinRedirect({
    state: { returnTo },
    extraQueryParams: uiLocale ? { ui_locales: uiLocale } : undefined,
  });
}

export async function completeSignIn(): Promise<string> {
  const user = await userManager().signinRedirectCallback();
  const state = user.state as { returnTo?: string } | undefined;
  return state?.returnTo && state.returnTo.startsWith("/") && !state.returnTo.startsWith("/auth/") ? state.returnTo : "/";
}

export function signOut(): Promise<void> {
  return userManager().signoutRedirect();
}

export async function dropLocalSession(): Promise<void> {
  await userManager().removeUser();
}
