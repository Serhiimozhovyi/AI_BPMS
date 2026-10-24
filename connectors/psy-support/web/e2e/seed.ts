import { APIRequestContext, request } from '@playwright/test';
import { E2E } from './env';

/** Виклик tool'а бота через `/mcp` (stateless HTTP) — так записи створює справжній бот. */
async function callTool(
    api: APIRequestContext,
    name: string,
    args: Record<string, unknown>,
): Promise<Record<string, unknown>> {
    const response = await api.post('/mcp', {
        headers: {
            Authorization: `Bearer ${E2E.mcpToken}`,
            Accept: 'application/json, text/event-stream',
        },
        data: { jsonrpc: '2.0', id: 1, method: 'tools/call', params: { name, arguments: args } },
    });
    if (!response.ok()) {
        throw new Error(`${name}: HTTP ${response.status()}`);
    }
    const body = await response.text();
    const json = body.trimStart().startsWith('{')
        ? body
        : (body.split('\n').find((l) => l.startsWith('data:')) ?? '').slice(5);
    const result = (JSON.parse(json) as { result: { structuredContent: Record<string, unknown> } })
        .result.structuredContent;
    if (result['ok'] !== true) {
        throw new Error(`${name}: ${String(result['code'])}`);
    }
    return result;
}

export interface Seed {
    own: { psychologist: string; phone: string; person: string };
    peer: { psychologist: string; phone: string; person: string };
    psychologistAccount: { id: number; email: string };
}

/** Дані через REST координатора і tools бота: два психологи, у кожного запис людини з телефоном. */
export async function seed(): Promise<Seed> {
    const api = await request.newContext({ baseURL: E2E.url });
    const login = await api.post('/api/v1/auth/login', { data: E2E.coordinator });
    if (!login.ok()) {
        throw new Error(`login: ${login.status()}`);
    }

    const post = async <T>(url: string, data: unknown): Promise<T> => {
        const r = await api.post(`/api/v1${url}`, { data });
        if (!r.ok()) {
            throw new Error(`${url}: ${r.status()} ${await r.text()}`);
        }
        return (await r.json()) as T;
    };

    const own = { psychologist: 'Олена Коваль', phone: '380501112233', person: 'Марія' };
    const peer = { psychologist: 'Петро Бондар', phone: '380674445566', person: 'Іван' };
    const ownPsy = await post<{ id: number }>('/psychologists', {
        name: own.psychologist,
        active: true,
    });
    const peerPsy = await post<{ id: number }>('/psychologists', {
        name: peer.psychologist,
        active: true,
    });
    const account = await post<{ id: number }>('/staff', {
        email: 'olena@e2e.test',
        role: 'psychologist',
        psychologist_id: ownPsy.id,
    });

    // Слоти післязавтра о 10:00 і 12:00 за UTC — у межах вікна запису й перегляду колег.
    const day = new Date();
    day.setUTCDate(day.getUTCDate() + 2);
    const at = (hour: number) =>
        new Date(
            Date.UTC(day.getUTCFullYear(), day.getUTCMonth(), day.getUTCDate(), hour),
        ).toISOString();
    const ownSlot = await post<{ id: number }>('/slots', {
        psychologist_id: ownPsy.id,
        starts_at: at(10),
        duration_min: 50,
        format: 'онлайн',
    });
    const peerSlot = await post<{ id: number }>('/slots', {
        psychologist_id: peerPsy.id,
        starts_at: at(12),
        duration_min: 50,
        format: 'онлайн',
    });

    const people = [
        { id: 9100001, who: own, slot: ownSlot.id },
        { id: 9100002, who: peer, slot: peerSlot.id },
    ];
    for (const p of people) {
        await callTool(api, 'bot_start', {
            telegram_id: p.id,
            name: p.who.person,
            contact: { user_id: p.id, phone_number: `+${p.who.phone}` },
        });
        await callTool(api, 'bot_consult', { telegram_id: p.id, data: `c:ok:${p.slot}` });
    }

    await api.dispose();
    return { own, peer, psychologistAccount: { id: account.id, email: 'olena@e2e.test' } };
}
