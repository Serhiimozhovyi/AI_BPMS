// Бекенд для живих E2E (F2-20): міграції OpenBP.Data, потім OpenBP.Api і OpenBP.Worker як дочірні процеси.
// Запускається з playwright.live.config.ts (webServer); рішення вже зібране (`dotnet build`), тут — `--no-build`.
// Postgres має бути запущений заздалегідь: CI — service-контейнер, локально — `scripts/dev.sh up`.
import { spawn, spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../..');
/** Адреса Api; та сама — у proxy.conf.json dev-сервера. */
const API_URL = 'http://localhost:5080';
const env = { ...process.env, DOTNET_NOLOGO: '1', DOTNET_CLI_TELEMETRY_OPTOUT: '1' };

const migrate = spawnSync(
  'dotnet',
  ['tool', 'run', 'dotnet-ef', 'database', 'update', '--no-build', '--project', 'src/OpenBP.Data'],
  { cwd: root, env: { ...env, OPENBP_DB: env.ConnectionStrings__Default }, stdio: 'inherit' },
);
if (migrate.status !== 0) {
  console.error('[e2e-live] міграції не застосовано');
  process.exit(1);
}

const children = [
  spawn('dotnet', ['run', '--no-build', '--project', 'src/OpenBP.Api', '--', '--urls', API_URL], { cwd: root, env, stdio: 'inherit' }),
  spawn('dotnet', ['run', '--no-build', '--project', 'src/OpenBP.Worker'], { cwd: root, env, stdio: 'inherit' }),
];

let stopping = false;
function stop(code) {
  if (stopping) return;
  stopping = true;
  for (const child of children) child.kill('SIGTERM');
  setTimeout(() => process.exit(code), 3000).unref();
}

for (const child of children) {
  child.on('exit', (code) => {
    if (!stopping) console.error(`[e2e-live] ${child.spawnargs.join(' ')} завершився з кодом ${code}`);
    stop(code ?? 1);
  });
}
process.on('SIGTERM', () => stop(0));
process.on('SIGINT', () => stop(0));
