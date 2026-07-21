namespace Archivio.Domain;

public sealed class SystemRecord
{
    private SystemRecord() { }

    public SystemRecord(string name, string value)
    {
        Id = Guid.NewGuid();
        Name = name;
        Value = value;
        CreatedAtUtc = DateTime.UtcNow;
    }

    public Guid Id { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string Value { get; private set; } = string.Empty;
    public DateTime CreatedAtUtc { get; private set; }
}
