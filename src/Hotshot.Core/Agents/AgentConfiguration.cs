using System.Text.Json;

namespace Hotshot.Core.Agents;

public enum CodingAgent { CopilotCli, ClaudeCode, VsCode, Cursor, Codex }

public static class AgentConfiguration
{
    public static string Create(CodingAgent agent, string executable)
    {
        if (!Path.IsPathFullyQualified(executable)) throw new ArgumentException("MCP executable must be an absolute path.");
        var command = JsonSerializer.Serialize(executable);
        return agent switch
        {
            CodingAgent.Codex => $"[mcp_servers.hotshot]\ncommand = {command}\nargs = []\n",
            CodingAgent.VsCode => $$"""
                {
                  "servers": {
                    "hotshot": { "type": "stdio", "command": {{command}}, "args": [] }
                  }
                }
                """,
            CodingAgent.CopilotCli => $$"""
                {
                  "mcpServers": {
                    "hotshot": { "type": "local", "command": {{command}}, "args": [], "tools": ["*"] }
                  }
                }
                """,
            CodingAgent.ClaudeCode or CodingAgent.Cursor => $$"""
                {
                  "mcpServers": {
                    "hotshot": { "command": {{command}}, "args": [] }
                  }
                }
                """,
            _ => throw new ArgumentException("Unsupported coding agent."),
        };
    }

    public static string Location(CodingAgent agent) => agent switch
    {
        CodingAgent.CopilotCli => @"Merge into %USERPROFILE%\.copilot\mcp-config.json, or use the Hotshot marketplace plugin.",
        CodingAgent.ClaudeCode => @"Merge into the project's .mcp.json, or install the Hotshot marketplace plugin.",
        CodingAgent.VsCode => @"Merge into the project's .vscode\mcp.json. Review and trust the server when prompted.",
        CodingAgent.Cursor => @"Merge into the project's .cursor\mcp.json or Cursor's MCP settings.",
        CodingAgent.Codex => @"Merge into %USERPROFILE%\.codex\config.toml.",
        _ => throw new ArgumentException("Unsupported coding agent."),
    };
}
