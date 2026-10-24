// Збирає вихід `ng run web:element` (ESM: polyfills.js з $localize, main.js і чанки) в один файл dist/element/openbp-canvas.js (IIFE),
// який хост підключає звичайним <script>. CSS уже в JS: стилі компонентів ідуть у Shadow DOM елемента (F4-13).
import { build } from 'esbuild';
import { readdirSync, statSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { gzipSync } from 'node:zlib';
import { readFileSync } from 'node:fs';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');
const browser = join(root, 'dist/element/browser');
const outfile = join(root, 'dist/element/openbp-canvas.js');

const css = readdirSync(browser).filter((f) => f.endsWith('.css'));
if (css.length > 0) throw new Error(`Глобальні стилі в збірці елемента (${css.join(', ')}): стилі мають бути в компонентах.`);

await build({
  // Порядок як у index.html SPA: спершу polyfills ($localize), потім застосунок.
  stdin: { contents: "import './polyfills.js';\nimport './main.js';\n", resolveDir: browser, sourcefile: 'openbp-canvas.entry.js' },
  bundle: true,
  format: 'iife',
  minify: true,
  legalComments: 'none',
  target: 'es2022',
  outfile,
  logLevel: 'warning',
});

const size = statSync(outfile).size;
const gzip = gzipSync(readFileSync(outfile)).length;
console.log(`openbp-canvas.js: ${(size / 1024).toFixed(1)} KB, gzip ${(gzip / 1024).toFixed(1)} KB`);
