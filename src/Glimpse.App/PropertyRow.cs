namespace Glimpse.App;

/// <summary>One line of the properties panel, grouped by <see cref="Category"/>.</summary>
public sealed record PropertyRow(string Category, string Name, string Value);
