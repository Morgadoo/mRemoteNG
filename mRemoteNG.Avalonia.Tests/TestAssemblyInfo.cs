using Xunit;

// The UI tests share one main window, DI container and settings; never run them concurrently.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
