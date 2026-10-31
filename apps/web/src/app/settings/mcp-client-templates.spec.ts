import { isLocalMcpUrl, mcpClientTemplates, mcpCodexConfig, mcpCopilotStudioFields, mcpOpenAiApiTool, mcpVsCodeConfig } from './mcp-client-templates';

describe('mcp-client-templates', () => {
  const url = 'https://openbp.example.ua/mcp';

  it('isLocalMcpUrl — localhost, http і .local не підходять хмарним клієнтам', () => {
    expect(isLocalMcpUrl('http://localhost:4200/mcp')).toBe(true);
    expect(isLocalMcpUrl('https://127.0.0.1/mcp')).toBe(true);
    expect(isLocalMcpUrl('http://openbp.example.ua/mcp')).toBe(true);
    expect(isLocalMcpUrl('https://mac.local/mcp')).toBe(true);
    expect(isLocalMcpUrl(url)).toBe(false);
    expect(isLocalMcpUrl('не адреса')).toBe(true);
  });

  it('VS Code — формат servers/inputs без ключа у файлі', () => {
    const cfg = JSON.parse(mcpVsCodeConfig(url));
    expect(cfg.servers.openbp.url).toBe(url);
    expect(cfg.servers.openbp.headers.Authorization).toBe('Bearer ${input:ai-bpms-key}');
    expect(cfg.inputs).toEqual([{ type: 'promptString', id: 'ai-bpms-key', description: 'AI_BPMS API key (obp_…)', password: true }]);
    expect(mcpVsCodeConfig(url)).not.toContain('obp_x');
  });

  it('Copilot Studio — заголовок Authorization і значення з Bearer', () => {
    const text = mcpCopilotStudioFields(url, 'obp_x');
    expect(text).toContain(`Server URL: ${url}`);
    expect(text).toContain('Type: Header');
    expect(text).toContain('Header name: Authorization');
    expect(text).toContain('Bearer obp_x');
  });

  it('Codex і OpenAI API — ключ у змінній середовища / authorization без Bearer', () => {
    expect(mcpCodexConfig(url, 'obp_x')).toContain('bearer_token_env_var = "OPENBP_API_KEY"');
    expect(mcpCodexConfig(url, 'obp_x')).toContain('export OPENBP_API_KEY=obp_x');
    const tool = JSON.parse(mcpOpenAiApiTool(url, 'obp_x')).tools[0];
    expect(tool).toMatchObject({ type: 'mcp', server_url: url, authorization: 'obp_x' });
  });

  it('список шаблонів — унікальні id, у кожного кроки, без фрагмента лише з попередженням', () => {
    const list = mcpClientTemplates(url, 'obp_x');
    expect(new Set(list.map((t) => t.id)).size).toBe(list.length);
    expect(list[0].id).toBe('desktop');
    for (const t of list) {
      expect(t.steps.length).toBeGreaterThan(0);
      if (t.snippet === null) expect(t.warning).toBeTruthy();
    }
    expect(list.filter((t) => t.needsPublicUrl).map((t) => t.id)).toEqual(['claude-ai', 'copilot-studio', 'openai-api', 'chatgpt']);
  });
});
