import Fastify, { type FastifyInstance } from 'fastify';
import pg from 'pg';
import { registerErrorHandler } from '@docbridge/shared';
import type { Config } from './config.js';

export function buildApp(config: Config): FastifyInstance {
  const app = Fastify({ logger: true });
  registerErrorHandler(app);

  const pool = new pg.Pool({ connectionString: config.databaseUrl, max: 5 });
  app.addHook('onClose', async () => {
    await pool.end();
  });

  // Liveness: process is up.
  app.get('/health', async () => ({ status: 'ok', service: 'docbridge-api' }));

  // Readiness: this service's own credentials reach its own logical database (ADR-009).
  app.get('/health/ready', async () => {
    await pool.query('SELECT 1');
    return { status: 'ready', service: 'docbridge-api' };
  });

  return app;
}
