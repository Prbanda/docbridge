import { describe, expect, it } from 'vitest';
import { contentTypeFor, MAX_BYTES, MAX_FILES } from './fileRules';

const file = (name: string) => new File(['x'], name);

describe('client-side file rules (mirror of the server allow-list)', () => {
  it('maps allowed extensions to their content type', () => {
    expect(contentTypeFor(file('protocol.pdf'))).toBe('application/pdf');
    expect(contentTypeFor(file('CONSENT.DOCX'))).toBe('application/vnd.openxmlformats-officedocument.wordprocessingml.document');
    expect(contentTypeFor(file('budget.xlsx'))).toBe('application/vnd.openxmlformats-officedocument.spreadsheetml.sheet');
    expect(contentTypeFor(file('scan.jpeg'))).toBe('image/jpeg');
    expect(contentTypeFor(file('scan.png'))).toBe('image/png');
  });

  it('rejects executables and unknown types', () => {
    expect(contentTypeFor(file('setup.exe'))).toBeNull();
    expect(contentTypeFor(file('script.sh'))).toBeNull();
    expect(contentTypeFor(file('noextension'))).toBeNull();
  });

  it('matches the requirement limits', () => {
    expect(MAX_FILES).toBe(100);
    expect(MAX_BYTES).toBe(1024 ** 3);
  });
});
