import { config, Counts, getToken, JobSummary, TaskRow } from './api';

export type JobEvent =
  | { type: 'subscribed'; jobId: string }
  | { type: 'error'; code: string }
  | {
      type: 'task_update';
      jobId: string;
      taskId: string;
      status: TaskRow['status'];
      failureReason: string | null;
      counts: Counts;
      aggregateStatus: JobSummary['aggregateStatus'];
    };

/**
 * WebSocket client for one job (ADR-007). On every (re)subscribe ack the caller refetches once and then applies
 * pushes: that is the whole catch-up story, so no server-side replay buffer is needed (TDD 3.3).
 */
export function openJobSocket(jobId: string, onEvent: (e: JobEvent) => void, onState: (connected: boolean) => void) {
  let socket: WebSocket | null = null;
  let closed = false;
  let attempt = 0;
  let timer: ReturnType<typeof setTimeout> | undefined;

  const connect = () => {
    const token = getToken();
    if (!token || closed) return;
    socket = new WebSocket(`${config.wsUrl}?token=${encodeURIComponent(token)}`);
    socket.onopen = () => {
      attempt = 0;
      onState(true);
      socket!.send(JSON.stringify({ action: 'subscribe', jobId }));
    };
    socket.onmessage = (msg) => onEvent(JSON.parse(msg.data) as JobEvent);
    socket.onclose = () => {
      onState(false);
      if (closed) return;
      attempt++;
      timer = setTimeout(connect, Math.min(30_000, 1000 * 2 ** Math.min(attempt, 5)));
    };
  };
  connect();

  return () => {
    closed = true;
    clearTimeout(timer);
    socket?.close();
  };
}
