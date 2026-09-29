import { describe, expect, it } from 'vitest';
import { buildApp } from './app.js';

const app = buildApp({ port: 0, databaseUrl: 'postgres://unused:unused@localhost:1/unused' });

describe('docbridge-api service skeleton', () => {
  it('responds on /health without touching the database', async () => {
    const res = await app.inject({ method: 'GET', url: '/health' });
    expect(res.statusCode).toBe(200);
    expect(res.json()).toEqual({ status: 'ok', service: 'docbridge-api' });
  });

  it('returns the canonical error shape for unknown routes', async () => {
    const res = await app.inject({ method: 'GET', url: '/nope' });
    expect(res.statusCode).toBe(404);
    expect(res.json()).toEqual({ error: { code: 'NOT_FOUND', message: 'Route not found' } });
  });
});
