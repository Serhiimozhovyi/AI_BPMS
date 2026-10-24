import { McpToolInfo } from '../connectors/connectors-api.service';
import { NodeTypeInfo } from '../nodes/nodes-api.service';
import { WorkflowNode } from '../workflows/workflows-api.service';

/**
 * Незаповнені обов'язкові параметри вузла до збереження (F3-57): `required` схеми параметрів, обов'язкові
 * credentials (`CredentialRequirement.required`) і — для `mcp.tool`/`mcp.poll` — `required` з `inputSchema` tool'а
 * (ключі `arguments.<name>`). Порожнє — немає ключа, `null` або рядок із самих пробілів.
 * Порядок — як у формі: спершу поля схеми, далі credentials, далі аргументи tool'а.
 */
export function missingFields(node: WorkflowNode, descriptor: NodeTypeInfo | undefined, tool?: McpToolInfo | null): string[] {
  const params = node.parameters ?? {};
  const result: string[] = [];
  const add = (key: string, value: unknown) => {
    if (isEmpty(value) && !result.includes(key)) result.push(key);
  };
  for (const key of requiredOf(descriptor?.parametersSchema)) add(key, params[key]);
  for (const c of descriptor?.credentials ?? []) {
    if (c.required) add(c.parameter, params[c.parameter]);
  }
  if (tool) {
    const args = params['arguments'];
    const values = args && typeof args === 'object' && !Array.isArray(args) ? (args as Record<string, unknown>) : {};
    for (const key of requiredOf(tool.inputSchema)) add(`arguments.${key}`, values[key]);
  }
  return result;
}

/** Незаповнені поля всіх вузлів сценарію: id вузла → ключі; вузли без пропусків не потрапляють. */
export function missingFieldsByNode(
  nodes: readonly WorkflowNode[],
  descriptorOf: (node: WorkflowNode) => NodeTypeInfo | undefined,
  toolOf: (node: WorkflowNode) => McpToolInfo | null = () => null,
): Map<string, string[]> {
  const result = new Map<string, string[]>();
  for (const node of nodes) {
    if (node.disabled) continue;
    const missing = missingFields(node, descriptorOf(node), toolOf(node));
    if (missing.length > 0) result.set(node.id, missing);
  }
  return result;
}

function requiredOf(schema: Record<string, unknown> | null | undefined): string[] {
  const required = schema?.['required'];
  return Array.isArray(required) ? required.filter((r): r is string => typeof r === 'string') : [];
}

function isEmpty(value: unknown): boolean {
  return value === undefined || value === null || (typeof value === 'string' && value.trim() === '');
}
