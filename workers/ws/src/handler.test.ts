import { describe, expect, it } from 'vitest';
import type { APIGatewayProxyWebsocketEventV2 } from 'aws-lambda';
import { handler } from './handler.js';

describe('ws handler (stub)', () => {
  it('accepts lifecycle events', async () => {
    const event = {
      requestContext: { routeKey: '$connect', connectionId: 'c-1' },
    } as APIGatewayProxyWebsocketEventV2;
    const res = await handler(event);
    expect(res).toEqual({ statusCode: 200 });
  });
});
