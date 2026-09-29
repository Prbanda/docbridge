/** All configuration from the environment — nothing hardcoded (requirements: Operability). */
export interface Config {
  port: number;
  databaseUrl: string;
}

export function loadConfig(env: NodeJS.ProcessEnv = process.env): Config {
  const databaseUrl = env.DATABASE_URL;
  if (!databaseUrl) {
    throw new Error('DATABASE_URL is required');
  }
  return {
    port: Number(env.PORT ?? 3001),
    databaseUrl,
  };
}
