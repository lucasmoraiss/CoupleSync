// The tests share process-level state (environment variables used by the host) and one container: run them one at a time.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
