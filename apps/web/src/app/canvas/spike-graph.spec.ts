import { demoGraph, largeGraph } from './spike-graph';

describe('spike-graph', () => {
  it('демо-граф — три вузли з прикладу формату, з’єднані ланцюжком', () => {
    const graph = demoGraph();

    expect(graph.nodes.map((n) => n.type)).toEqual(['core.webhook', 'ai.llm', 'mcp.tool']);
    expect(graph.nodes[0].inputs).toEqual([]);
    expect(graph.nodes[2].outputs).toEqual(['main', 'error']);
    expect(graph.edges).toHaveLength(2);
  });

  it('великий граф має рівно N вузлів з унікальними id і ребрами лише між існуючими вузлами', () => {
    const graph = largeGraph(200);
    const ids = new Set(graph.nodes.map((n) => n.id));

    expect(graph.nodes).toHaveLength(200);
    expect(ids.size).toBe(200);
    for (const edge of graph.edges) {
      expect(ids.has(edge.source)).toBe(true);
      expect(ids.has(edge.target)).toBe(true);
      const source = graph.nodes.find((n) => n.id === edge.source)!;
      expect(edge.sourceOutput).toBeLessThan(source.outputs.length);
    }
  });

  it('кожен ряд великого графа починається з тригера без входів', () => {
    const graph = largeGraph(30, 10);
    const triggers = graph.nodes.filter((n) => n.inputs.length === 0);

    expect(triggers.map((n) => n.id)).toEqual(['n0', 'n10', 'n20']);
    expect(triggers.every((n) => n.group === 'trigger')).toBe(true);
  });
});
