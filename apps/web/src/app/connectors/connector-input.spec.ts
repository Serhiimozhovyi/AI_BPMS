import { ConnectorFormValue, argumentsSkeleton, parseArguments, parseKeyValues, parseLines, statusLabel, toConnectorInput } from './connector-input';

const blank: ConnectorFormValue = {
  name: '',
  transport: 'stdio',
  command: '',
  arguments: '',
  environment: '',
  workingDirectory: '',
  url: '',
  headers: '',
  credentialId: '',
};

describe('connector-input', () => {
  it('parseLines відкидає порожні рядки і пробіли', () => {
    expect(parseLines(' a \n\n b\r\n')).toEqual(['a', 'b']);
  });

  it('parseKeyValues розбирає за першим роздільником і відхиляє рядки без ключа', () => {
    expect(parseKeyValues('A=1\nB = x=y', '=')).toEqual({ A: '1', B: 'x=y' });
    expect(parseKeyValues('X-Api: v1', ':')).toEqual({ 'X-Api': 'v1' });
    expect(parseKeyValues('broken', '=')).toBeNull();
    expect(parseKeyValues('=v', '=')).toBeNull();
    expect(parseKeyValues('', ':')).toEqual({});
  });

  it('toConnectorInput для stdio не надсилає полів http', () => {
    const input = toConnectorInput({
      ...blank,
      name: ' fs ',
      command: 'npx',
      arguments: '-y\n@mcp/fs',
      environment: 'LOG=debug',
      url: 'https://ignored',
    });
    expect(input).toEqual({
      name: 'fs',
      transport: 'stdio',
      credentialId: null,
      config: { command: 'npx', arguments: ['-y', '@mcp/fs'], environment: { LOG: 'debug' }, workingDirectory: null },
    });
  });

  it('toConnectorInput для http з credential', () => {
    const input = toConnectorInput({
      ...blank,
      name: '1С',
      transport: 'http',
      url: 'https://mcp.example/mcp',
      credentialId: '0b9a1c64-8d1e-4f7a-9c3b-2d5e6f708192',
    });
    expect(input).toEqual({
      name: '1С',
      transport: 'http',
      credentialId: '0b9a1c64-8d1e-4f7a-9c3b-2d5e6f708192',
      config: { url: 'https://mcp.example/mcp', headers: null },
    });
  });

  it('argumentsSkeleton заповнює лише обовʼязкові властивості', () => {
    expect(
      argumentsSkeleton({
        type: 'object',
        properties: {
          s: { type: 'string' },
          n: { type: 'integer' },
          b: { type: ['boolean', 'null'] },
          a: { type: 'array' },
          o: { type: 'object' },
          d: { type: 'string', default: 'x' },
          opt: { type: 'string' },
        },
        required: ['s', 'n', 'b', 'a', 'o', 'd'],
      }),
    ).toEqual({ s: '', n: 0, b: false, a: [], o: {}, d: 'x' });
    expect(argumentsSkeleton(undefined)).toEqual({});
  });

  it('parseArguments приймає лише JSON-обʼєкт', () => {
    expect(parseArguments('')).toEqual({});
    expect(parseArguments('{"a":1}')).toEqual({ a: 1 });
    expect(parseArguments('[1]')).toBeNull();
    expect(parseArguments('null')).toBeNull();
    expect(parseArguments('{oops')).toBeNull();
  });

  it('statusLabel українською', () => {
    expect(statusLabel('ok')).toBe('Підключено');
    expect(statusLabel('error')).toBe('Помилка');
    expect(statusLabel('new')).toBe('Не перевірено');
  });
});
