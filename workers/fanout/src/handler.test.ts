import { describe, expect, it } from 'vitest';
import type { SQSEvent } from 'aws-lambda';
import { handler, parseJobMessage } from './handler.js';

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

describe('parseJobMessage', () => {
  it('parses a valid job message', () => {
    expect(parseJobMessage('{"jobId":"j-1"}')).toEqual({ jobId: 'j-1' });
  });

  it('rejects non-JSON and missing jobId', () => {
    expect(() => parseJobMessage('not json')).toThrow(/not JSON/);
    expect(() => parseJobMessage('{"foo":1}')).toThrow(/missing jobId/);
  });
});

describe('fanout handler (stub)', () => {
  it('acks valid messages and reports only malformed ones as batch failures', async () => {
    const res = await handler(sqsEvent(['{"jobId":"j-1"}', 'garbage']));
    expect(res.batchItemFailures).toEqual([{ itemIdentifier: 'm-1' }]);
  });
});
