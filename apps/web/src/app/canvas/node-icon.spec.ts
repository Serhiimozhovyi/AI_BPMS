import { SAMPLE_DESCRIPTORS } from './sample-workflows';
import { ICON_TYPES, nodeIcon } from './node-icon';

describe('node-icon (F3-91)', () => {
  it('значок за типом, інакше за групою', () => {
    expect(nodeIcon('core.loop', 'logic')).toBe('repeat');
    expect(nodeIcon('core.future', 'transform')).toBe('tune');
    expect(nodeIcon('mcp.tool', 'integration')).toBe('extension');
    expect(nodeIcon('whatever', 'unknown')).toBe('help');
  });

  it('кожен тип з каталогу вузлів має власний значок, значки різних типів не збігаються без потреби', () => {
    const missing = SAMPLE_DESCRIPTORS.map((d) => d.type).filter((t) => !ICON_TYPES.includes(t));
    expect(missing).toEqual([]);
    const triggers = ['core.manualTrigger', 'core.schedule', 'core.webhook', 'core.formTrigger'].map((t) => nodeIcon(t, 'trigger'));
    expect(new Set(triggers).size).toBe(triggers.length);
  });
});
