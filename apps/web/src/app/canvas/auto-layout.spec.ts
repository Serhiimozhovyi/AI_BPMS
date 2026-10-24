import { WorkflowDefinition, WorkflowNode, emptyWorkflowDefinition } from '../workflows/workflows-api.service';
import { LAYOUT_GAP_X, layoutLeftToRight } from './auto-layout';
import { parsePosition } from './canvas-model';
import dailyReport from '../../../../../examples/daily-report.json';
import helloManual from '../../../../../examples/hello-manual.json';
import orderTotal from '../../../../../examples/order-total.json';
import psyAlerts from '../../../../../examples/psy-support-alerts.json';
import psyBot from '../../../../../examples/psy-support-bot.json';
import psyScheduler from '../../../../../examples/psy-support-scheduler.json';
import telegramOllama from '../../../../../examples/telegram-1c-ollama.json';
import telegram1c from '../../../../../examples/telegram-1c.json';

const WIDTH = 220;
/** Висота «як на канвасі»: різна для різних вузлів, щоб перевірити накладання. */
const height = (n: WorkflowNode) => 72 + (n.type === 'core.switch' ? 64 : n.type === 'core.if' ? 16 : 0);

const EXAMPLES: Record<string, WorkflowDefinition> = {
  dailyReport: dailyReport as unknown as WorkflowDefinition,
  helloManual: helloManual as unknown as WorkflowDefinition,
  orderTotal: orderTotal as unknown as WorkflowDefinition,
  psyAlerts: psyAlerts as unknown as WorkflowDefinition,
  psyBot: psyBot as unknown as WorkflowDefinition,
  psyScheduler: psyScheduler as unknown as WorkflowDefinition,
  telegramOllama: telegramOllama as unknown as WorkflowDefinition,
  telegram1c: telegram1c as unknown as WorkflowDefinition,
};

function boxes(def: WorkflowDefinition) {
  return def.nodes.map((n) => {
    const { x, y } = parsePosition(n.position);
    return { id: n.id, x, y, w: WIDTH, h: height(n) };
  });
}

describe('layoutLeftToRight', () => {
  for (const [name, def] of Object.entries(EXAMPLES)) {
    it(`${name}: без накладань, ребра йдуть зліва направо, ребра й параметри ті самі, результат детермінований`, () => {
      const laid = layoutLeftToRight(def, WIDTH, height);
      const placed = boxes(laid);
      for (let i = 0; i < placed.length; i++) {
        for (let j = i + 1; j < placed.length; j++) {
          const a = placed[i];
          const b = placed[j];
          const overlap = a.x < b.x + b.w && b.x < a.x + a.w && a.y < b.y + b.h && b.y < a.y + a.h;
          expect(overlap, `${a.id} і ${b.id}`).toBe(false);
        }
      }
      const x = new Map(placed.map((p) => [p.id, p.x]));
      for (const e of laid.connections) expect(x.get(e.to.node)!).toBeGreaterThanOrEqual(x.get(e.from.node)! + WIDTH + LAYOUT_GAP_X);
      expect(laid.connections).toBe(def.connections);
      laid.nodes.forEach((n, i) => expect({ ...n, position: def.nodes[i].position }).toEqual(def.nodes[i]));
      expect(layoutLeftToRight(def, WIDTH, height)).toEqual(laid);
      // Вирівняне вже не змінюється.
      expect(layoutLeftToRight(laid, WIDTH, height)).toBe(laid);
    });
  }

  it('гілки IF: наступники стоять у колонці за порядком виходів; ізольований вузол — у першій колонці', () => {
    const node = (id: string, x: number, y: number, type = 'core.set'): WorkflowNode => ({ id, name: id, type, typeVersion: 1, position: [x, y], parameters: {} });
    const def: WorkflowDefinition = {
      ...emptyWorkflowDefinition('IF'),
      nodes: [node('t', 500, 500, 'core.manualTrigger'), node('if', 0, 0, 'core.if'), node('no', 900, 0), node('yes', 900, 900), node('lone', 20, 30)],
      connections: [
        { from: { node: 't', output: 0 }, to: { node: 'if', input: 0 } },
        { from: { node: 'if', output: 1 }, to: { node: 'no', input: 0 } },
        { from: { node: 'if', output: 0 }, to: { node: 'yes', input: 0 } },
      ],
    };
    const laid = boxes(layoutLeftToRight(def, WIDTH, height));
    const at = (id: string) => laid.find((b) => b.id === id)!;
    expect(at('t').x).toBe(0);
    expect(at('lone').x).toBe(0);
    expect(at('if').x).toBe(WIDTH + LAYOUT_GAP_X);
    expect(at('yes').x).toBe(at('no').x);
    expect(at('yes').y).toBeLessThan(at('no').y);
  });

  it('порожній сценарій — без змін', () => {
    const def = emptyWorkflowDefinition('Порожній');
    expect(layoutLeftToRight(def, WIDTH, height)).toBe(def);
  });
});
