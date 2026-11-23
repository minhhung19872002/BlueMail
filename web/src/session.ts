import { createContext, useContext } from "react";
import type { Me } from "./api/types";
import type { UserFormat } from "./format";

export const MeContext = createContext<Me | null>(null);

export function useMe(): Me {
  const me = useContext(MeContext);
  if (!me) throw new Error("useMe outside of an authenticated tree");
  return me;
}

export function useUserFormat(): UserFormat {
  const me = useMe();
  return { locale: me.locale, timeZone: me.timeZone, hourCycle: me.hourCycle };
}

export function useCan(permission: string): boolean {
  return useMe().permissions.includes(permission);
}
