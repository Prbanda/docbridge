/**
 * The one error shape every service returns (TDD "API Design & Contracts"):
 *   { "error": { "code": "string", "message": "string" } }
 */
export interface ApiErrorBody {
  error: {
    code: string;
    message: string;
  };
}

/** Failure-reason codes surfaced on tasks (TDD steps 2.4/2.5). */
export const TASK_FAILURE_CODES = [
  'FILE_NOT_UPLOADED',
  'NOT_AUTHORIZED_AT_DELIVERY',
  'CHECKSUM_MISMATCH',
  'RETRIES_EXHAUSTED',
] as const;
export type TaskFailureCode = (typeof TASK_FAILURE_CODES)[number];

/**
 * Application error carrying an HTTP status and a stable machine-readable code.
 * Services convert any thrown AppError into the ApiErrorBody shape.
 */
export class AppError extends Error {
  readonly statusCode: number;
  readonly code: string;

  constructor(statusCode: number, code: string, message: string) {
    super(message);
    this.name = 'AppError';
    this.statusCode = statusCode;
    this.code = code;
  }

  toBody(): ApiErrorBody {
    return { error: { code: this.code, message: this.message } };
  }

  static badRequest(code: string, message: string): AppError {
    return new AppError(400, code, message);
  }

  static unauthorized(message = 'Authentication required'): AppError {
    return new AppError(401, 'UNAUTHORIZED', message);
  }

  static forbidden(message = 'Not allowed'): AppError {
    return new AppError(403, 'FORBIDDEN', message);
  }

  /** 404 is also used where existence must not be confirmed (TDD M4). */
  static notFound(message = 'Not found'): AppError {
    return new AppError(404, 'NOT_FOUND', message);
  }

  static conflict(code: string, message: string): AppError {
    return new AppError(409, code, message);
  }
}

/** Convert any thrown value into the canonical error body + status. */
export function toErrorResponse(err: unknown): { statusCode: number; body: ApiErrorBody } {
  if (err instanceof AppError) {
    return { statusCode: err.statusCode, body: err.toBody() };
  }
  // Never leak internals: unknown errors become an opaque 500.
  return {
    statusCode: 500,
    body: { error: { code: 'INTERNAL', message: 'Internal server error' } },
  };
}
