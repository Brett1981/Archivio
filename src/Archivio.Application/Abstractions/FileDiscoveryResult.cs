namespace Archivio.Application.Abstractions;

public sealed record FileDiscoveryResult(
    string RootPath,
    IReadOnlyList<DiscoveredFile> Files,
    IReadOnlyList<FileDiscoveryIssue> Issues,
    DateTime StartedAtUtc,
    DateTime CompletedAtUtc)
{
    public TimeSpan Duration => CompletedAtUtc - StartedAtUtc;
}
