import type { SQSBatchResponse, SQSEvent } from 'aws-lambda';

/** Job queue message: one fan-out message per submission (ADR-008). */
export interface JobMessage {
  jobId: string;
}

export function parseJobMessage(body: string): JobMessage {
  let parsed: unknown;
  try {
    parsed = JSON.parse(body);
  } catch {
    throw new Error(`job message is not JSON: ${body.slice(0, 100)}`);
  }
  const jobId =
    typeof parsed === 'object' && parsed !== null
      ? (parsed as Record<string, unknown>).jobId
      : undefined;
  if (typeof jobId !== 'string' || jobId.length === 0) {
    throw new Error('job message missing jobId');
  }
  return { jobId };
}

/**
 * M0 stub. M2 (step 2.3): load task IDs for the job, SendMessageBatch in 10s
 * to the task queue, body {taskId} only. Idempotent on redelivery.
 */
export async function handler(event: SQSEvent): Promise<SQSBatchResponse> {
  const batchItemFailures: SQSBatchResponse['batchItemFailures'] = [];
  for (const record of event.Records) {
    try {
      const message = parseJobMessage(record.body);
      console.log(JSON.stringify({ msg: 'fanout stub received job message', jobId: message.jobId }));
    } catch (err) {
      console.error(JSON.stringify({ msg: 'fanout parse failure', err: String(err) }));
      batchItemFailures.push({ itemIdentifier: record.messageId });
    }
  }
  return { batchItemFailures };
}
