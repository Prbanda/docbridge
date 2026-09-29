import type { JWTPayload, JWTVerifyGetKey } from 'jose';
import { createLocalJWKSet, createRemoteJWKSet, jwtVerify } from 'jose';
import type { FastifyReply, FastifyRequest } from 'fastify';
import { AppError } from './errors.js';

/** Claims we rely on. User tokens: sub = user id. Service tokens: sub = client id (ADR-013). */
export interface VerifiedToken {
  sub: string;
  email?: string;
  scope?: string;
  tokenUse: 'user' | 'service';
  payload: JWTPayload;
}

export interface JwtVerifierOptions {
  /** e.g. http://auth:3001/.well-known/jwks.json */
  jwksUrl: string;
  /** Pre-resolved key set (tests / offline). Overrides jwksUrl when given. */
  localJwks?: Parameters<typeof createLocalJWKSet>[0];
  issuer?: string;
}

export interface JwtVerifier {
  verify(token: string): Promise<VerifiedToken>;
}

/**
 * One JWT verification implementation for every service and worker (TDD step 0.1).
 * RS256 against the auth service's JWKS; key rollover handled by `kid` via jose.
 */
export function createJwtVerifier(opts: JwtVerifierOptions): JwtVerifier {
  const getKey: JWTVerifyGetKey = opts.localJwks
    ? createLocalJWKSet(opts.localJwks)
    : createRemoteJWKSet(new URL(opts.jwksUrl));

  return {
    async verify(token: string): Promise<VerifiedToken> {
      let payload: JWTPayload;
      try {
        ({ payload } = await jwtVerify(token, getKey, {
          algorithms: ['RS256'],
          ...(opts.issuer ? { issuer: opts.issuer } : {}),
        }));
      } catch {
        throw AppError.unauthorized('Invalid or expired token');
      }
      if (typeof payload.sub !== 'string' || payload.sub.length === 0) {
        throw AppError.unauthorized('Token missing subject');
      }
      return {
        sub: payload.sub,
        email: typeof payload.email === 'string' ? payload.email : undefined,
        scope: typeof payload.scope === 'string' ? payload.scope : undefined,
        tokenUse: payload.token_use === 'service' ? 'service' : 'user',
        payload,
      };
    },
  };
}

declare module 'fastify' {
  interface FastifyRequest {
    auth?: VerifiedToken;
  }
}

export interface AuthGuardOptions {
  /** Require a service token with this scope (ADR-013 routes). */
  requireScope?: string;
}

/**
 * Fastify preHandler enforcing a valid Bearer token; attaches request.auth.
 * Services re-validate even behind the gateway authorizer (defense in depth,
 * TDD "Security & access") — and the platform must, since workers bypass the gateway.
 */
export function createAuthGuard(verifier: JwtVerifier, guard: AuthGuardOptions = {}) {
  return async function authGuard(request: FastifyRequest, reply: FastifyReply): Promise<void> {
    try {
      const header = request.headers.authorization;
      if (!header?.startsWith('Bearer ')) {
        throw AppError.unauthorized();
      }
      const verified = await verifier.verify(header.slice('Bearer '.length));
      if (guard.requireScope) {
        const scopes = verified.scope?.split(' ') ?? [];
        if (verified.tokenUse !== 'service' || !scopes.includes(guard.requireScope)) {
          throw AppError.forbidden('Missing required scope');
        }
      }
      request.auth = verified;
    } catch (err) {
      const { statusCode, body } =
        err instanceof AppError
          ? { statusCode: err.statusCode, body: err.toBody() }
          : { statusCode: 401 as const, body: AppError.unauthorized().toBody() };
      await reply.status(statusCode).send(body);
    }
  };
}
