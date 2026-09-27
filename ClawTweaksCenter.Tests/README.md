# Regression checks

Run on Windows x64 with the .NET 10 SDK:

```powershell
dotnet run --project ClawTweaksCenter.Tests -c Release
dotnet run --project ClawTweaksCenter.Tests -c Release -- History
dotnet run --project ClawTweaksCenter.Tests -c Release -- --list
```

This dependency-free console runner executes `[RegressionTest]` static methods sequentially and
returns a nonzero exit code on a failure or an empty selection. Methods may return `Task`.
`[RegressionProbe("name")]` methods are explicit child-process probes, reached with `--probe name`.
Keep probes read-only except for their own synthetic fixtures.

Tests reference the application assembly; they do not launch Center or touch its live stores.
Use disposable synthetic directories, in-memory transports and process commands created by the test.
Never invoke actual installation, package removal, hardware control or a user's backup/restore.
Do not add secret keys, machine logs, or copies of private helper code.

Each bug-fix commit includes its regression and records the observed red/green result in its commit
message. The generated build directories remain ignored. NuGet's existing LiteDB 4.x advisory is
documented in the application project; these tests do not suppress its audit.
