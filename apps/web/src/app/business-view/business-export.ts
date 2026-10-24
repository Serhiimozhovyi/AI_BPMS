/**
 * Експорт бізнес-вигляду (F3-66): SVG зі стилями, вбудованими в атрибути (CSS-змінні теми й класи компонента
 * поза сторінкою не працюють), і PNG, намальований з того самого SVG.
 */

const STYLE_PROPS = [
  'fill',
  'stroke',
  'stroke-width',
  'stroke-dasharray',
  'font-family',
  'font-size',
  'font-weight',
  'letter-spacing',
  'text-transform',
  'text-anchor',
] as const;

/** Копія SVG з обчисленими стилями в атрибутах `style`, без трансформації масштабу екрана. */
export function standaloneSvg(svg: SVGSVGElement): string {
  const clone = svg.cloneNode(true) as SVGSVGElement;
  const source = [svg, ...Array.from(svg.querySelectorAll('*'))];
  const target = [clone, ...Array.from(clone.querySelectorAll('*'))];
  source.forEach((el, i) => {
    const computed = getComputedStyle(el);
    const style = STYLE_PROPS.map((p) => [p, computed.getPropertyValue(p)] as const)
      .filter(([, v]) => v !== '')
      .map(([p, v]) => `${p}:${v}`)
      .join(';');
    const node = target[i] as SVGElement;
    node.removeAttribute('class');
    node.removeAttribute('tabindex');
    node.removeAttribute('role');
    if (style) node.setAttribute('style', style);
  });
  clone.style.removeProperty('transform');
  clone.setAttribute('xmlns', 'http://www.w3.org/2000/svg');
  return `<?xml version="1.0" encoding="UTF-8"?>\n${new XMLSerializer().serializeToString(clone).replace(/<!--[\s\S]*?-->/g, '')}`;
}

export function exportSvg(svg: SVGSVGElement, name: string): void {
  download(new Blob([standaloneSvg(svg)], { type: 'image/svg+xml' }), `${name}.svg`);
}

/** PNG у 2× для чіткості в документах і листах. */
export async function exportPng(svg: SVGSVGElement, name: string): Promise<void> {
  const width = Number(svg.getAttribute('width')) || 800;
  const height = Number(svg.getAttribute('height')) || 600;
  const url = URL.createObjectURL(new Blob([standaloneSvg(svg)], { type: 'image/svg+xml' }));
  try {
    const image = new Image();
    image.src = url;
    await image.decode();
    const canvas = document.createElement('canvas');
    canvas.width = width * 2;
    canvas.height = height * 2;
    const ctx = canvas.getContext('2d');
    if (!ctx) return;
    ctx.fillStyle = '#ffffff';
    ctx.fillRect(0, 0, canvas.width, canvas.height);
    ctx.drawImage(image, 0, 0, canvas.width, canvas.height);
    const blob = await new Promise<Blob | null>((resolve) => canvas.toBlob(resolve, 'image/png'));
    if (blob) download(blob, `${name}.png`);
  } finally {
    URL.revokeObjectURL(url);
  }
}

function download(blob: Blob, fileName: string): void {
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = fileName;
  a.style.display = 'none';
  document.body.appendChild(a);
  a.click();
  a.remove();
  // Браузер читає blob асинхронно: відкликаємо посилання із запасом, інакше файл без імені чи порожній.
  setTimeout(() => URL.revokeObjectURL(url), 10_000);
}
