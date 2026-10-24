import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

/** Префікс REST бекенду (docs/psy-support.md §7.1). */
export const API = '/api/v1';

export type Role = 'coordinator' | 'psychologist';

/** `GET auth/me`. */
export interface Me {
    id: number;
    email: string;
    role: Role;
    psychologist_id?: number | null;
    timezone: string;
}

export interface Psychologist {
    id: number;
    name: string;
    email?: string | null;
    phone?: string | null;
    active: boolean;
    linked_to_telegram: boolean;
}

export interface StaffAccount {
    id: number;
    email: string;
    role: Role;
    psychologist_id?: number | null;
    disabled: boolean;
    password_set: boolean;
    last_login_at?: string | null;
}

export interface Invite {
    token: string;
    expires_at: string;
}

export interface Slot {
    id: number;
    psychologist_id: number;
    psychologist_name: string;
    starts_at: string;
    duration_min: number;
    format: string;
    status: 'open' | 'closed';
    source: 'template' | 'manual';
    booking_id?: number | null;
}

export interface SlotTemplate {
    id: number;
    psychologist_id: number;
    weekday: number;
    time: string;
    duration_min: number;
    format: string;
    valid_from?: string | null;
    valid_to?: string | null;
}

export interface Booking {
    id: number;
    slot_id: number;
    psychologist_id: number;
    psychologist_name: string;
    starts_at: string;
    duration_min: number;
    format: string;
    status: string;
    person_name?: string | null;
    phone?: string | null;
}

export interface EventItem {
    id: number;
    title: string;
    description?: string | null;
    starts_at: string;
    format?: string | null;
    host?: string | null;
    contact?: string | null;
    capacity: number;
    status: 'draft' | 'open' | 'closed';
    taken: number;
    notice?: string | null;
    notice_sent_at?: string | null;
}

export interface BstSession {
    id: number;
    psychologist_id: number;
    psychologist_name: string;
    starts_at: string;
    format?: string | null;
    capacity: number;
    status: 'open' | 'closed';
    taken: number;
    notice?: string | null;
    notice_sent_at?: string | null;
}

export interface Participant {
    id: number;
    name?: string | null;
    phone?: string | null;
    status: string;
    created_at: string;
    feedback?: number | null;
}

export interface Material {
    id: number;
    title: string;
    body?: string | null;
    link?: string | null;
    status: 'draft' | 'published' | 'archived';
    published_at?: string | null;
    views: number;
}

export interface Counter {
    metric: string;
    value: number;
}

export interface AuditEntry {
    id: number;
    at: string;
    actor: string;
    action: string;
    entity?: string | null;
    entity_id?: number | null;
    details?: string | null;
}

export interface UserHit {
    telegram_id: number;
    name?: string | null;
    phone?: string | null;
    registered_at: string;
}

export interface NoticeResult {
    code: string;
    queued: number;
}

/** Тонка обгортка над REST: один метод — один ендпойнт. */
@Injectable({ providedIn: 'root' })
export class Api {
    private readonly http = inject(HttpClient);

    me(): Observable<Me> {
        return this.http.get<Me>(`${API}/auth/me`);
    }

    login(email: string, password: string): Observable<Me> {
        return this.http.post<Me>(`${API}/auth/login`, { email, password });
    }

    logout(): Observable<void> {
        return this.http.post<void>(`${API}/auth/logout`, {});
    }

    acceptInvite(token: string, password: string): Observable<Me> {
        return this.http.post<Me>(`${API}/auth/invite`, { token, password });
    }

    psychologists(): Observable<Psychologist[]> {
        return this.http.get<Psychologist[]>(`${API}/psychologists`);
    }

    createPsychologist(body: {
        name: string;
        phone?: string | null;
        email?: string | null;
        active: boolean;
    }): Observable<{ id: number }> {
        return this.http.post<{ id: number }>(`${API}/psychologists`, body);
    }

    updatePsychologist(
        id: number,
        body: { name: string; phone?: string | null; email?: string | null; active: boolean },
    ): Observable<void> {
        return this.http.put<void>(`${API}/psychologists/${id}`, body);
    }

    staff(): Observable<StaffAccount[]> {
        return this.http.get<StaffAccount[]>(`${API}/staff`);
    }

    createStaff(body: {
        email: string;
        role: Role;
        psychologist_id?: number | null;
    }): Observable<{ id: number; invite: Invite }> {
        return this.http.post<{ id: number; invite: Invite }>(`${API}/staff`, body);
    }

    newInvite(id: number): Observable<Invite> {
        return this.http.post<Invite>(`${API}/staff/${id}/invite`, {});
    }

    setStaffDisabled(id: number, disabled: boolean): Observable<void> {
        return this.http.post<void>(`${API}/staff/${id}/disabled`, {}, { params: { disabled } });
    }

    slots(from: string, to: string, psychologistId?: number | null): Observable<Slot[]> {
        let params = new HttpParams().set('from', from).set('to', to);
        if (psychologistId) {
            params = params.set('psychologistId', psychologistId);
        }
        return this.http.get<Slot[]>(`${API}/slots`, { params });
    }

    createSlot(body: {
        psychologist_id: number;
        starts_at: string;
        duration_min?: number | null;
        format?: string | null;
    }): Observable<{ id: number }> {
        return this.http.post<{ id: number }>(`${API}/slots`, body);
    }

    setSlotStatus(id: number, status: 'open' | 'closed'): Observable<void> {
        return this.http.put<void>(`${API}/slots/${id}/status`, { status });
    }

    deleteSlot(id: number): Observable<void> {
        return this.http.delete<void>(`${API}/slots/${id}`);
    }

    templates(psychologistId?: number | null): Observable<SlotTemplate[]> {
        const params = psychologistId
            ? new HttpParams().set('psychologistId', psychologistId)
            : undefined;
        return this.http.get<SlotTemplate[]>(`${API}/slot-templates`, { params });
    }

    createTemplate(body: {
        psychologist_id: number;
        weekday: number;
        time: string;
        duration_min?: number | null;
        format?: string | null;
    }): Observable<{ id: number }> {
        return this.http.post<{ id: number }>(`${API}/slot-templates`, body);
    }

    deleteTemplate(id: number): Observable<void> {
        return this.http.delete<void>(`${API}/slot-templates/${id}`);
    }

    generateSlots(days: number): Observable<{ created: number }> {
        return this.http.post<{ created: number }>(`${API}/slots/generate`, { days });
    }

    bookings(from: string, to: string): Observable<Booking[]> {
        return this.http.get<Booking[]>(`${API}/bookings`, { params: { from, to } });
    }

    cancelBooking(id: number): Observable<void> {
        return this.http.post<void>(`${API}/bookings/${id}/cancel`, {});
    }

    events(from?: string): Observable<EventItem[]> {
        return this.http.get<EventItem[]>(`${API}/events`, { params: from ? { from } : {} });
    }

    saveEvent(
        id: number | null,
        body: Omit<EventItem, 'id' | 'taken' | 'notice_sent_at'>,
    ): Observable<unknown> {
        return id
            ? this.http.put(`${API}/events/${id}`, body)
            : this.http.post(`${API}/events`, body);
    }

    registrations(id: number): Observable<Participant[]> {
        return this.http.get<Participant[]>(`${API}/events/${id}/registrations`);
    }

    sendEventNotice(id: number): Observable<NoticeResult> {
        return this.http.post<NoticeResult>(`${API}/events/${id}/notice`, {});
    }

    bst(from?: string): Observable<BstSession[]> {
        return this.http.get<BstSession[]>(`${API}/bst`, { params: from ? { from } : {} });
    }

    saveBst(
        id: number | null,
        body: {
            psychologist_id: number;
            starts_at: string;
            format?: string | null;
            capacity?: number | null;
            status: string;
            notice?: string | null;
        },
    ): Observable<unknown> {
        return id ? this.http.put(`${API}/bst/${id}`, body) : this.http.post(`${API}/bst`, body);
    }

    bstParticipants(
        id: number,
    ): Observable<{ participants: Participant[]; waitlist: Participant[] }> {
        return this.http.get<{ participants: Participant[]; waitlist: Participant[] }>(
            `${API}/bst/${id}/participants`,
        );
    }

    sendBstNotice(id: number): Observable<NoticeResult> {
        return this.http.post<NoticeResult>(`${API}/bst/${id}/notice`, {});
    }

    materials(): Observable<Material[]> {
        return this.http.get<Material[]>(`${API}/materials`);
    }

    saveMaterial(
        id: number | null,
        body: { title: string; body?: string | null; link?: string | null; status: string },
    ): Observable<unknown> {
        return id
            ? this.http.put(`${API}/materials/${id}`, body)
            : this.http.post(`${API}/materials`, body);
    }

    settings(): Observable<Record<string, string>> {
        return this.http.get<Record<string, string>>(`${API}/settings`);
    }

    saveSetting(key: string, value: string): Observable<void> {
        return this.http.put<void>(`${API}/settings/${key}`, { value });
    }

    counters(): Observable<Counter[]> {
        return this.http.get<Counter[]>(`${API}/counters`);
    }

    audit(before?: number): Observable<AuditEntry[]> {
        return this.http.get<AuditEntry[]>(`${API}/audit`, {
            params: before ? { before, limit: 100 } : { limit: 100 },
        });
    }

    searchUsers(q: string): Observable<UserHit[]> {
        return this.http.get<UserHit[]>(`${API}/users`, { params: { q } });
    }

    deleteUser(telegramId: number): Observable<void> {
        return this.http.post<void>(`${API}/users/${telegramId}/delete`, {});
    }
}
