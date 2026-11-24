import { api } from "../../api/client";

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
  list: (from: string, to: string) => api.get<CalendarEvent[]>(`/api/v1/calendar/events?from=${encodeURIComponent(from)}&to=${encodeURIComponent(to)}`),
  create: (e: EventInput) => api.post<CalendarEvent>("/api/v1/calendar/events", e),
  update: (id: string, e: EventInput) => api.put<CalendarEvent>(`/api/v1/calendar/events/${id}`, e),
  remove: (id: string, occurrence?: string) =>
    api.delete<boolean>(`/api/v1/calendar/events/${id}${occurrence ? `?occurrence=${encodeURIComponent(occurrence)}` : ""}`),
};
