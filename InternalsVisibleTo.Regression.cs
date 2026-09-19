using System.Runtime.CompilerServices;

// Allows the isolated headless regression to exercise the same internal
// manual-only editor and persistence path as the production view model.
[assembly: InternalsVisibleTo("FrameMaskLayerRegression")]
