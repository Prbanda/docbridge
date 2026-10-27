// Client-side mirror of the server rules (the server enforces them again): requirements' supported types,
// no executables, 1 GB ceiling, 100 files per job.
export const ALLOWED: Record<string, string[]> = {
  'application/pdf': ['.pdf'],
  'application/vnd.openxmlformats-officedocument.wordprocessingml.document': ['.docx'],
  'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet': ['.xlsx'],
  'image/png': ['.png'],
  'image/jpeg': ['.jpg', '.jpeg'],
};
export const MAX_FILES = 100;
export const MAX_BYTES = 1024 ** 3;

export function contentTypeFor(file: File): string | null {
  const dot = file.name.lastIndexOf('.');
  if (dot < 0) return null;
  const ext = file.name.slice(dot).toLowerCase();
  const match = Object.entries(ALLOWED).find(([, exts]) => exts.includes(ext));
  return match ? match[0] : null;
}
