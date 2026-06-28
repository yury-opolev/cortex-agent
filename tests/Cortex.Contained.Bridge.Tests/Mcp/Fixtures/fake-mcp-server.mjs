// Minimal MCP stdio server for integration testing the Bridge's StdioMcpServerConnection.
// Speaks newline-delimited JSON-RPC 2.0 over stdio (the MCP stdio framing). Implements just
// enough of the protocol — initialize, tools/list, tools/call — to exercise a real handshake.
//
// Tools:
//   echo        -> returns the `text` argument verbatim.
//   reveal_env  -> returns the value of process.env.MCP_TEST_SECRET (proves env-secret injection).
//   hidden_tool -> exists so allow-list filtering can be verified.
import process from 'node:process';
import readline from 'node:readline';

const TOOLS = [
  {
    name: 'echo',
    description: 'Echoes the provided text.',
    inputSchema: { type: 'object', properties: { text: { type: 'string' } }, required: ['text'] },
  },
  {
    name: 'reveal_env',
    description: 'Returns the MCP_TEST_SECRET environment variable.',
    inputSchema: { type: 'object', properties: {} },
  },
  {
    name: 'hidden_tool',
    description: 'A tool that should be excluded by an allow-list.',
    inputSchema: { type: 'object', properties: {} },
  },
];

function send(message) {
  process.stdout.write(JSON.stringify(message) + '\n');
}

function reply(id, result) {
  send({ jsonrpc: '2.0', id, result });
}

function callTool(name, args) {
  if (name === 'echo') {
    return { content: [{ type: 'text', text: String((args && args.text) ?? '') }], isError: false };
  }
  if (name === 'reveal_env') {
    return { content: [{ type: 'text', text: String(process.env.MCP_TEST_SECRET ?? '') }], isError: false };
  }
  if (name === 'hidden_tool') {
    return { content: [{ type: 'text', text: 'hidden' }], isError: false };
  }
  return { content: [{ type: 'text', text: `unknown tool: ${name}` }], isError: true };
}

const rl = readline.createInterface({ input: process.stdin });
rl.on('line', (line) => {
  const trimmed = line.trim();
  if (trimmed.length === 0) {
    return;
  }

  let message;
  try {
    message = JSON.parse(trimmed);
  } catch {
    return;
  }

  const { id, method, params } = message;

  // Notifications (no id) require no response.
  if (id === undefined || id === null) {
    return;
  }

  switch (method) {
    case 'initialize':
      reply(id, {
        protocolVersion: (params && params.protocolVersion) || '2025-06-18',
        capabilities: { tools: {} },
        serverInfo: { name: 'fake-mcp-server', version: '1.0.0' },
      });
      break;
    case 'ping':
      reply(id, {});
      break;
    case 'tools/list':
      reply(id, { tools: TOOLS });
      break;
    case 'tools/call':
      reply(id, callTool(params && params.name, params && params.arguments));
      break;
    default:
      send({ jsonrpc: '2.0', id, error: { code: -32601, message: `method not found: ${method}` } });
      break;
  }
});
