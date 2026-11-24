import { api } from "../../api/client";
import type { FreeBusy } from "./calendarView";

// The user's calendar (docs/05-api.md "Calendar").

export interface Attendee {
  name: string | null;
  address: string;
  response: string | null;
}

/** One occurrence of an event in the requested window. */
export interface CalendarEvent {
  id: string;
  uid: string;
  summary: string;
  location: string | null;
  description: string | null;
  start: string;
  end: string;
  allDay: boolean;
  timeZone: string;
  recurrenceRule: string | null;
  recurring: boolean;
  isOrganizer: boolean;
  organizerEmail: string | null;
  organizerName: string | null;
  attendees: Attendee[];
  myResponse: string | null;
  status: string;
  reminderMinutes: number | null;
  color: string | null;
  /** Set on events of a calendar shared with the user (whose it is and what they may do). */
  owner?: SharedCalendar;
}

export interface SharedCalendar {
  userId: string;
  email: string;
  name: string;
  permission: "VIEW" | "EDIT";
}

export interface CalendarShares {
  mine: SharedCalendar[];
  withMe: SharedCalendar[];
}

export interface EventInput {
  summary: string;
  location: string | null;
  description: string | null;
  start: string;
  end: string;
  allDay: boolean;
  timeZone: string;
  recurrenceRule: string | null;
  attendees: { name: string | null; address: string }[];
  reminderMinutes: number | null;
  color: string | null;
}

export const calendarKeys = { all: ["calendar"] as const, range: (from: string, to: string) => ["calendar", from, to] as const };

export const calendarApi = {
  list: (from: string, to: string, owner?: string) =>
    api.get<CalendarEvent[]>(`/api/v1/calendar/events?from=${encodeURIComponent(from)}&to=${encodeURIComponent(to)}${owner ? `&owner=${owner}` : ""}`),
  shares: () => api.get<CalendarShares>("/api/v1/calendar/shares"),
  setShare: (email: string, permission: "VIEW" | "EDIT" | "NONE") => api.put<CalendarShares>("/api/v1/calendar/shares", { email, permission }),
  create: (e: EventInput) => api.post<CalendarEvent>("/api/v1/calendar/events", e),
  update: (id: string, e: EventInput, owner?: string) => api.put<CalendarEvent>(`/api/v1/calendar/events/${id}${owner ? `?owner=${owner}` : ""}`, e),
  freeBusy: (addresses: string[], from: string, to: string) =>
    api.get<FreeBusy[]>(`/api/v1/calendar/freebusy?${addresses.map((a) => "address=" + encodeURIComponent(a)).join("&")}&from=${encodeURIComponent(from)}&to=${encodeURIComponent(to)}`),
  remove: (id: string, occurrence?: string, owner?: string) => {
    const q = new URLSearchParams();
    if (occurrence) q.set("occurrence", occurrence);
    if (owner) q.set("owner", owner);
    return api.delete<boolean>(`/api/v1/calendar/events/${id}${q.size > 0 ? "?" + q.toString() : ""}`);
  },
};
