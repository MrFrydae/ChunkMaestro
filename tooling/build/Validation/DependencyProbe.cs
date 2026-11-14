// Compile-time dependency probe only; never shipped or counted as a test.
namespace ChunkMaestro.BuildValidation;

internal static class DependencyProbe
{
    internal static Type[] ResolvedTypes =>
    [
        typeof(Avalonia.Application),
        typeof(Microsoft.EntityFrameworkCore.DbContext),
        typeof(Xunit.FactAttribute),
    ];
}
