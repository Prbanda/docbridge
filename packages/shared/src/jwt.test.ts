import { describe, expect, it, beforeAll } from 'vitest';
import { SignJWT, exportJWK, generateKeyPair } from 'jose';
import Fastify from 'fastify';
import { createAuthGuard, createJwtVerifier, type JwtVerifier } from './jwt.js';

let verifier: JwtVerifier;
let sign: (payload: Record<string, unknown>, expiresIn?: string) => Promise<string>;
let signWithWrongKey: () => Promise<string>;

beforeAll(async () => {
  const { publicKey, privateKey } = await generateKeyPair('RS256');
  const jwk = { ...(await exportJWK(publicKey)), kid: 'test-key', alg: 'RS256', use: 'sig' };
  verifier = createJwtVerifier({ jwksUrl: 'http://unused.local', localJwks: { keys: [jwk] } });

  sign = (payload, expiresIn = '15m') =>
    new SignJWT(payload)
      .setProtectedHeader({ alg: 'RS256', kid: 'test-key' })
      .setIssuedAt()
      .setExpirationTime(expiresIn)
      .sign(privateKey);

  signWithWrongKey = async () => {
    const other = await generateKeyPair('RS256');
    return new SignJWT({ sub: 'user-1' })
      .setProtectedHeader({ alg: 'RS256', kid: 'test-key' })
      .setIssuedAt()
      .setExpirationTime('15m')
      .sign(other.privateKey);
  };
});

describe('createJwtVerifier', () => {
  it('verifies a valid user token and extracts claims', async () => {
    const token = await sign({ sub: 'user-1', email: 'a@b.c' });
    const result = await verifier.verify(token);
    expect(result.sub).toBe('user-1');
    expect(result.email).toBe('a@b.c');
    expect(result.tokenUse).toBe('user');
  });

  it('identifies service tokens via token_use claim (ADR-013)', async () => {
    const token = await sign({ sub: 'delivery-worker', scope: 'documents:ingest', token_use: 'service' });
    const result = await verifier.verify(token);
    expect(result.tokenUse).toBe('service');
    expect(result.scope).toBe('documents:ingest');
  });

  it('rejects an expired token', async () => {
    const token = await sign({ sub: 'user-1' }, '-1m');
    await expect(verifier.verify(token)).rejects.toMatchObject({ statusCode: 401 });
  });

  it('rejects a token signed by an unknown key', async () => {
    await expect(verifier.verify(await signWithWrongKey())).rejects.toMatchObject({ statusCode: 401 });
  });

  it('rejects a token without a subject', async () => {
    const token = await sign({ email: 'a@b.c' });
    await expect(verifier.verify(token)).rejects.toMatchObject({ statusCode: 401 });
  });
});

describe('createAuthGuard (Fastify preHandler)', () => {
  function buildApp(requireScope?: string) {
    const app = Fastify();
    app.get('/protected', { preHandler: createAuthGuard(verifier, { requireScope }) }, async (req) => ({
      sub: req.auth?.sub,
    }));
    return app;
  }

  it('rejects requests without a token — every endpoint requires auth', async () => {
    const res = await buildApp().inject({ method: 'GET', url: '/protected' });
    expect(res.statusCode).toBe(401);
    expect(res.json().error.code).toBe('UNAUTHORIZED');
  });

  it('accepts a valid Bearer token and attaches request.auth', async () => {
    const token = await sign({ sub: 'user-42' });
    const res = await buildApp().inject({
      method: 'GET',
      url: '/protected',
      headers: { authorization: `Bearer ${token}` },
    });
    expect(res.statusCode).toBe(200);
    expect(res.json().sub).toBe('user-42');
  });

  it('enforces scope on service routes', async () => {
    const userToken = await sign({ sub: 'user-1' });
    const wrongScope = await sign({ sub: 'svc', scope: 'other:scope', token_use: 'service' });
    const rightScope = await sign({ sub: 'svc', scope: 'documents:ingest', token_use: 'service' });

    const app = buildApp('documents:ingest');
    expect((await app.inject({ url: '/protected', headers: { authorization: `Bearer ${userToken}` } })).statusCode).toBe(403);
    expect((await app.inject({ url: '/protected', headers: { authorization: `Bearer ${wrongScope}` } })).statusCode).toBe(403);
    expect((await app.inject({ url: '/protected', headers: { authorization: `Bearer ${rightScope}` } })).statusCode).toBe(200);
  });
});
