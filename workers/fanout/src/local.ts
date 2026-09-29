/**
 * Local runner: long-polls the job queue on LocalStack and invokes the handler,
 * so the worker runs as a local process under docker compose (TDD step 0.1).
 */
import { DeleteMessageCommand, ReceiveMessageCommand, SQSClient } from '@aws-sdk/client-sqs';
import type { SQSEvent } from 'aws-lambda';
import { handler } from './handler.js';

const queueUrl = process.env.JOB_QUEUE_URL;
if (!queueUrl) {
  throw new Error('JOB_QUEUE_URL is required');
}

const sqs = new SQSClient({
  endpoint: process.env.AWS_ENDPOINT_URL,
  region: process.env.AWS_REGION ?? 'us-east-1',
});

console.log(`[fanout] polling ${queueUrl}`);
for (;;) {
  const { Messages } = await sqs.send(
    new ReceiveMessageCommand({ QueueUrl: queueUrl, MaxNumberOfMessages: 10, WaitTimeSeconds: 20 }),
  );
  if (!Messages?.length) continue;

  const event: SQSEvent = {
    Records: Messages.map((m) => ({
      messageId: m.MessageId ?? '',
      receiptHandle: m.ReceiptHandle ?? '',
      body: m.Body ?? '',
      attributes: {} as SQSEvent['Records'][number]['attributes'],
      messageAttributes: {},
      md5OfBody: m.MD5OfBody ?? '',
      eventSource: 'aws:sqs',
      eventSourceARN: 'local',
      awsRegion: process.env.AWS_REGION ?? 'us-east-1',
    })),
  };

  const { batchItemFailures } = await handler(event);
  const failed = new Set(batchItemFailures.map((f) => f.itemIdentifier));
  for (const m of Messages) {
    if (m.MessageId && !failed.has(m.MessageId) && m.ReceiptHandle) {
      await sqs.send(new DeleteMessageCommand({ QueueUrl: queueUrl, ReceiptHandle: m.ReceiptHandle }));
    }
  }
}
