namespace RebornLauncher.Desktop;

/// <summary>
/// One row of the prerequisites list. It carries its own display text so the template needs no
/// converters, and is public because compiled bindings resolve the type from the XAML.
/// </summary>
public sealed record PrerequisiteRow(
    string Id,
    string DisplayName,
    string Purpose,
    string Detail,
    string Glyph,
    string ActionLabel,
    bool CanInstall);
