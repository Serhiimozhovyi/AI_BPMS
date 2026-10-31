import { mcpClaudeCodeCommand, mcpClientConfig, mcpDesktopConfig } from './api-keys-api.service';

/**
 * Шаблон підключення ШІ-клієнта до `/mcp` (F3-118): вкладка в «Підключити ШІ-агента» — куди вставити, кроки й готовий
 * фрагмент з адресою і ключем. Довідник з поясненнями — docs/guide/ai-connectors.md.
 */
export interface McpClientTemplate {
  id: string;
  icon: string;
  label: string;
  /** Куди вставити фрагмент (файл, екран клієнта). */
  where: string;
  steps: string[];
  /** Готовий фрагмент; `null` — клієнт поки не підключається (див. `warning`). */
  snippet: string | null;
  /** Клієнт ходить до `/mcp` зі своїх серверів: потрібна публічна HTTPS-адреса AI_BPMS. */
  needsPublicUrl: boolean;
  warning?: string;
}

/** Адреса не підходить хмарним клієнтам: не HTTPS або локальна. */
export function isLocalMcpUrl(mcpUrl: string): boolean {
  try {
    const u = new URL(mcpUrl);
    return u.protocol !== 'https:' || ['localhost', '127.0.0.1', '[::1]'].includes(u.hostname) || u.hostname.endsWith('.local');
  } catch {
    return true;
  }
}

/** GitHub Copilot у VS Code: `.vscode/mcp.json`; ключ не пишемо у файл — VS Code спитає його і збереже в сховищі секретів. */
export function mcpVsCodeConfig(mcpUrl: string): string {
  return JSON.stringify(
    {
      inputs: [{ type: 'promptString', id: 'ai-bpms-key', description: 'AI_BPMS API key (obp_…)', password: true }],
      servers: { openbp: { type: 'http', url: mcpUrl, headers: { Authorization: 'Bearer ${input:ai-bpms-key}' } } },
    },
    null,
    2,
  );
}

/** Microsoft Copilot Studio: значення полів майстра «Model Context Protocol» і підключення. */
export function mcpCopilotStudioFields(mcpUrl: string, key: string): string {
  return [
    'Server name: AI_BPMS',
    'Server description: AI_BPMS — business processes: list, build, validate, run workflows, read executions, complete approval tasks',
    `Server URL: ${mcpUrl}`,
    'Authentication: API key',
    'Type: Header',
    'Header name: Authorization',
    `API key (connection): Bearer ${key}`,
  ].join('\n');
}

/** claude.ai — «Add custom connector» з «Request headers». */
export function mcpClaudeAiFields(mcpUrl: string, key: string): string {
  return [
    'Name: AI_BPMS',
    `Remote MCP server URL: ${mcpUrl}`,
    'Authentication: No sign-in',
    `Request headers: authorization = Bearer ${key}`,
  ].join('\n');
}

/** Codex: `~/.codex/config.toml` + змінна середовища з ключем. */
export function mcpCodexConfig(mcpUrl: string, key: string): string {
  return `# ~/.codex/config.toml\n[mcp_servers.openbp]\nurl = "${mcpUrl}"\nbearer_token_env_var = "OPENBP_API_KEY"\n\n# перед запуском codex:\nexport OPENBP_API_KEY=${key}`;
}

/** OpenAI API (Responses): tool `mcp`. */
export function mcpOpenAiApiTool(mcpUrl: string, key: string): string {
  return JSON.stringify(
    { tools: [{ type: 'mcp', server_label: 'openbp', server_url: mcpUrl, authorization: key, require_approval: 'always' }] },
    null,
    2,
  );
}

/** Усі шаблони у порядку вкладок; перший — вкладка за замовчуванням. */
export function mcpClientTemplates(mcpUrl: string, key: string): McpClientTemplate[] {
  return [
    {
      id: 'desktop',
      icon: '🖥️',
      label: 'Claude Desktop',
      where: $localize`:@@mcp.tpl.desktop.where:Settings → Developer → Edit Config (claude_desktop_config.json)`,
      steps: [
        $localize`:@@mcp.tpl.desktop.s1:Потрібен Node.js 18+ (міст mcp-remote запускається через npx).`,
        $localize`:@@mcp.tpl.desktop.s2:Вставте фрагмент у claude_desktop_config.json і повністю перезапустіть Claude Desktop.`,
        $localize`:@@mcp.tpl.desktop.s3:У чаті «+» → «Connectors» з'явиться openbp з переліком tools.`,
      ],
      snippet: mcpDesktopConfig(mcpUrl, key),
      needsPublicUrl: false,
    },
    {
      id: 'code',
      icon: '⌨️',
      label: 'Claude Code',
      where: $localize`:@@mcp.tpl.code.where:Термінал`,
      steps: [
        $localize`:@@mcp.tpl.code.s1:Виконайте команду в терміналі.`,
        $localize`:@@mcp.tpl.code.s2:claude mcp list покаже openbp: ✓ Connected; у сесії /mcp — його tools.`,
      ],
      snippet: mcpClaudeCodeCommand(mcpUrl, key),
      needsPublicUrl: false,
    },
    {
      id: 'claude-ai',
      icon: '☁️',
      label: 'claude.ai',
      where: $localize`:@@mcp.tpl.claudeAi.where:claude.ai → Customize → Connectors → Add custom connector`,
      steps: [
        $localize`:@@mcp.tpl.claudeAi.s1:Заповніть поля діалогу значеннями з фрагмента.`,
        $localize`:@@mcp.tpl.claudeAi.s2:Потрібен розділ «Request headers» (бета, відкрита не всім організаціям); без нього користуйтеся Claude Desktop.`,
        $localize`:@@mcp.tpl.claudeAi.s3:У чаті «+» → «Connectors» увімкніть AI_BPMS.`,
      ],
      snippet: mcpClaudeAiFields(mcpUrl, key),
      needsPublicUrl: true,
    },
    {
      id: 'vscode',
      icon: '🧑‍💻',
      label: 'Copilot (VS Code)',
      where: $localize`:@@mcp.tpl.vscode.where:.vscode/mcp.json у проєкті або «MCP: Open User Configuration»`,
      steps: [
        $localize`:@@mcp.tpl.vscode.s1:Вставте фрагмент у mcp.json. Ключа у файлі немає — файл можна комітити.`,
        $localize`:@@mcp.tpl.vscode.s2:Command Palette → «MCP: List Servers» → openbp → Start. VS Code попросить ключ: вставте obp_… — він збережеться в сховищі секретів.`,
        $localize`:@@mcp.tpl.vscode.s3:Copilot Chat → режим Agent → значок інструментів: tools openbp.`,
      ],
      snippet: mcpVsCodeConfig(mcpUrl),
      needsPublicUrl: false,
    },
    {
      id: 'copilot-studio',
      icon: '🤖',
      label: 'Copilot Studio',
      where: $localize`:@@mcp.tpl.studio.where:Агент → Tools → Add a tool → New tool → Model Context Protocol`,
      steps: [
        $localize`:@@mcp.tpl.studio.s1:Заповніть майстер значеннями з фрагмента: Authentication — API key, Type — Header, Header name — Authorization.`,
        $localize`:@@mcp.tpl.studio.s2:Create → «Create a new connection» → у поле ключа вставте значення разом із «Bearer » і пробілом.`,
        $localize`:@@mcp.tpl.studio.s3:Add to agent. Агента можна опублікувати в Microsoft 365 Copilot і Teams.`,
      ],
      snippet: mcpCopilotStudioFields(mcpUrl, key),
      needsPublicUrl: true,
    },
    {
      id: 'codex',
      icon: '🧩',
      label: 'Codex',
      where: $localize`:@@mcp.tpl.codex.where:~/.codex/config.toml і змінна середовища`,
      steps: [
        $localize`:@@mcp.tpl.codex.s1:Додайте блок у config.toml; ключ — у змінній OPENBP_API_KEY, не у файлі.`,
        $localize`:@@mcp.tpl.codex.s2:Запустіть codex — /mcp покаже openbp і його tools.`,
      ],
      snippet: mcpCodexConfig(mcpUrl, key),
      needsPublicUrl: false,
    },
    {
      id: 'openai-api',
      icon: '🔧',
      label: 'OpenAI API',
      where: $localize`:@@mcp.tpl.openai.where:Тіло запиту Responses API (власний агент)`,
      steps: [
        $localize`:@@mcp.tpl.openai.s1:Додайте tool у кожен запит: OpenAI не зберігає authorization між запитами.`,
        $localize`:@@mcp.tpl.openai.s2:allowed_tools обмежує, які tools бачить модель; require_approval — які потребують підтвердження.`,
      ],
      snippet: mcpOpenAiApiTool(mcpUrl, key),
      needsPublicUrl: true,
    },
    {
      id: 'chatgpt',
      icon: '💬',
      label: 'ChatGPT',
      where: $localize`:@@mcp.tpl.chatgpt.where:Settings → Apps → Advanced settings → Developer mode`,
      steps: [
        $localize`:@@mcp.tpl.chatgpt.s1:Ті самі можливості від OpenAI вже зараз дають Codex і OpenAI API — сусідні вкладки.`,
      ],
      snippet: null,
      needsPublicUrl: true,
      warning: $localize`:@@mcp.tpl.chatgpt.warning:Поки не підключається: ChatGPT уміє лише OAuth або «без автентифікації», а /mcp AI_BPMS приймає тільки API-ключ. Не відкривайте /mcp без ключа — тоді будь-хто з адресою керуватиме сценаріями.`,
    },
    {
      id: 'other',
      icon: '🔌',
      label: $localize`:@@settings.mcp.other:Інше`,
      where: $localize`:@@mcp.tpl.other.where:Конфігурація MCP-клієнта (Cursor, Windsurf, .mcp.json тощо)`,
      steps: [
        $localize`:@@mcp.tpl.other.s1:Для клієнтів, що розуміють type: http і заголовки. Транспорт — Streamable HTTP, автентифікація — Authorization: Bearer obp_….`,
      ],
      snippet: mcpClientConfig(mcpUrl, key),
      needsPublicUrl: false,
    },
  ];
}
