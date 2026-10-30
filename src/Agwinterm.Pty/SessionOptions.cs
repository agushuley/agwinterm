namespace Agwinterm.Pty;

public sealed record WorkspaceOptions(
    string? DefaultDir = null,
    string? DefaultProfile = null,
    IReadOnlyDictionary<string, string>? Env = null);

public sealed record SessionCreateRequest(
    string? Name = null,
    string? Cwd = null,
    string? Workspace = null,
    string? Command = null,
    string? WorkspaceName = null,
    bool CreateWorkspace = false,
    string? Profile = null,
    bool NoSelect = false,
    bool Wait = false,
    string? Caller = null,
    string? CommandMode = null,
    IReadOnlyDictionary<string, string>? Env = null);
