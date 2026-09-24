namespace FolderTrail.App.Shared;

/// <summary>A value with its user-facing label, for combo boxes.</summary>
public sealed record Option<T>(T Value, string Label)
{
    public override string ToString() => Label;
}
