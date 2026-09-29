import { describe, expect, it } from 'vitest';
import { AppError, toErrorResponse } from './errors.js';

describe('AppError', () => {
  it('produces the canonical error body shape', () => {
    const err = AppError.badRequest('INVALID_FILE', 'file too large');
    expect(err.toBody()).toEqual({
      error: { code: 'INVALID_FILE', message: 'file too large' },
    });
    expect(err.statusCode).toBe(400);
  });

  it('maps unknown errors to an opaque 500 without leaking internals', () => {
    const { statusCode, body } = toErrorResponse(new Error('db password is hunter2'));
    expect(statusCode).toBe(500);
    expect(body.error.code).toBe('INTERNAL');
    expect(body.error.message).not.toContain('hunter2');
  });

  it('passes AppError through with its own status', () => {
    const { statusCode, body } = toErrorResponse(AppError.notFound());
    expect(statusCode).toBe(404);
    expect(body.error.code).toBe('NOT_FOUND');
  });
});
