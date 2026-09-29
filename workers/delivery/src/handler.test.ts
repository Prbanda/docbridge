import { describe, expect, it } from 'vitest';
import type { SQSEvent } from 'aws-lambda';
import { handler, parseTaskMessage } from './handler.js';

function sqsEvent(bodies: string[]): SQSEvent {
  return {
    Records: bodies.map((body, i) => ({
      messageId: `m-${i}`,
      receiptHandle: 'rh',
      body,
      attributes: {} as SQSEvent['Records'][number]['attributes'],
      messageAttributes: {},
      md5OfBody: '',
      eventSource: 'aws:sqs',
      eventSourceARN: 'arn:test',
      awsRegion: 'us-east-1',
    })),
  };
}

describe('parseTaskMessage', () => {
  it('parses a valid task message', () => {
    expect(parseTaskMessage('{"taskId":"t-1"}')).toEqual({ taskId: 't-1' });
  });

  it('rejects non-JSON and missing taskId', () => {
    expect(() => parseTaskMessage('nope')).toThrow(/not JSON/);
    expect(() => parseTaskMessage('{}')).toThrow(/missing taskId/);
  });
});

describe('delivery handler (stub)', () => {
  it('acks valid messages and reports only malformed ones as batch failures', async () => {
    const res = await handler(sqsEvent(['{"taskId":"t-1"}', '{}']));
    expect(res.batchItemFailures).toEqual([{ itemIdentifier: 'm-1' }]);
  });
});
