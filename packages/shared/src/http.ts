import type { FastifyInstance } from 'fastify';
import { toErrorResponse } from './errors.js';

/** Wire the canonical { error: { code, message } } shape into a Fastify app. */
export function registerErrorHandler(app: FastifyInstance): void {
  app.setErrorHandler((err, request, reply) => {
    const { statusCode, body } = toErrorResponse(err);
    if (statusCode >= 500) {
      request.log.error({ err }, 'unhandled error');
    }
    void reply.status(statusCode).send(body);
  });

  app.setNotFoundHandler((_request, reply) => {
    void reply.status(404).send({ error: { code: 'NOT_FOUND', message: 'Route not found' } });
  });
}
