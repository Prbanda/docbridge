import type { SQSBatchResponse, SQSEvent } from 'aws-lambda';

/** Task queue message: one message per task, body {taskId} only — no tokens in queues (TDD 2.3). */
export interface TaskMessage {
  taskId: string;
}

export function parseTaskMessage(body: string): TaskMessage {
  let parsed: unknown;
  try {
    parsed = JSON.parse(body);
  } catch {
    throw new Error(`task message is not JSON: ${body.slice(0, 100)}`);
  }
  const taskId =
    typeof parsed === 'object' && parsed !== null
      ? (parsed as Record<string, unknown>).taskId
      : undefined;
  if (typeof taskId !== 'string' || taskId.length === 0) {
    throw new Error('task message missing taskId');
  }
  return { taskId };
}

/**
 * M0 stub. M2 (step 2.4): conditional claim, HEAD staged object, stream
 * S3 → SHA-256 → platform POST /documents, outcome mapping, job close.
 * Permanent failures ack; transient failures are reported as batch failures
 * so SQS redelivers (visibility timeout + maxReceiveCount 3, ADR-005).
 */
export async function handler(event: SQSEvent): Promise<SQSBatchResponse> {
  const batchItemFailures: SQSBatchResponse['batchItemFailures'] = [];
  for (const record of event.Records) {
    try {
      const message = parseTaskMessage(record.body);
      console.log(JSON.stringify({ msg: 'delivery stub received task message', taskId: message.taskId }));
    } catch (err) {
      console.error(JSON.stringify({ msg: 'delivery parse failure', err: String(err) }));
      batchItemFailures.push({ itemIdentifier: record.messageId });
    }
  }
  return { batchItemFailures };
}
