# FindFast validation status

This file records reproducible validation evidence for the MVP acceptance criteria in `SPEC.md`. Results are not promoted from reduced scale to the one-million-file target.

## Automated verification

Run:

```powershell
dotnet build FindFast.sln -c Release
dotnet test tests/FindFast.Tests/FindFast.Tests.csproj -c Release
powershell -NoProfile -ExecutionPolicy Bypass -File installer/tests/Installer.Tests.ps1
dotnet test tests/FindFast.Tests/FindFast.Tests.csproj -c Release --collect:"XPlat Code Coverage" --results-directory TestResults
```

The suite includes deterministic literal and regex comparisons against independent reference scans. It compares path, line, column and matched text for indexed-prefilter, filtered-scan, case-insensitive, whole-word and path-filtered searches. MCP tests cover initialization, tool schemas, notifications, successful structured tool calls and method/tool errors.

Validation on 2026-09-03 (`DESKAMD`, Windows 10.0.26100, AMD64 Family 23 Model 113, 12 logical CPUs, .NET 8.0.19):

- Release build: passed with zero warnings and zero errors.
- .NET tests: 33 passed, 0 failed.
- Installer tests: passed.
- Coverage after the three new tests: 90.10% lines (701/778) and 79.14% branches (535/676).

## Performance validation

Reduced validation command:

```powershell
dotnet run -c Release --project benchmarks/FindFast.Benchmarks -- 10000 10 --validate
```

Measured results on the machine above:

| Scenario | Warm p95 |
|---|---:|
| Selective literal | 5.191 ms |
| Common literal, first 100 results | 99.642 ms |
| Short literal filtered scan | 3732.269 ms |
| Selective regex | 8.560 ms |
| Regex without a mandatory literal | 110.573 ms |
| Large-file literal | 33.514 ms |

Cancellation was observed in 0.433 ms and all 1,000 deliberately excluded files remained excluded. The first-page and cancellation targets passed at reduced scale.

Watcher visibility **failed**: a new file in the 10,000-file root was still not visible after 5,005.082 ms, against the 2,000 ms target. The current update path rebuilds and publishes the root snapshot, so this is a product scalability gap rather than a benchmark failure.

The one-million-file validation remains unproven and must not be marked complete. Run the following on reference hardware with sufficient time and disk capacity:

```powershell
dotnet run -c Release --project benchmarks/FindFast.Benchmarks -- 1000000 200 --validate
```
