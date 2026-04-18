// Disable parallel execution between test classes in this assembly.
// Integration tests use process-level environment variables (JWT__SECRET etc.) for JWT
// configuration. Parallel classes racing to set/unset these variables causes intermittent
// failures when one factory's Dispose clears the env var while another factory is building
// its host and reading configuration.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
