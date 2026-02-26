# SeleniumDomSnapshot

Captures and diffs DOM snapshots during Selenium test runs. When a test breaks with `NoSuchElementException` or an unexpected state, this library shows you **exactly what changed** in the page structure between the passing baseline and the failing run.

## Packages

| Package | Purpose |
|---|---|
| `SeleniumDomSnapshot` | Core library — snapshot capture, diffing, HTML reports, CI helpers |
| `SeleniumDomSnapshot.NUnit` | NUnit integration — `[DomSnapshot]` attribute for automatic capture |

## Quick Start

### 1. Install

```xml
<!-- In your test project -->
<PackageReference Include="SeleniumDomSnapshot" Version="1.0.0" />
<PackageReference Include="SeleniumDomSnapshot.NUnit" Version="1.0.0" />
```

### 2. Add the attribute

```csharp
using SeleniumDomSnapshot.NUnit;

[Test]
[DomSnapshot(baselineOnPass: true)]
public void Login_RedirectsToDashboard()
{
    driver.Navigate().GoToUrl("https://your-app.com/login");
    // ... test code ...
    Assert.That(driver.Url, Contains.Substring("dashboard"));
}
```

On the first **passing** run a baseline is saved. On any **failing** run the current DOM is captured and a colour-coded HTML diff report is written showing exactly what changed.

### 3. Manual control (more flexibility)

```csharp
var snapshots = new DomSnapshotManager("dom_snapshots", ignorePatterns: new[]
{
    DomSnapshotManager.IgnorePatterns.IsoTimestamps,
    DomSnapshotManager.IgnorePatterns.ReadableTimestamps,
    DomSnapshotManager.IgnorePatterns.CspNonces,
    // your own app-specific patterns:
    // @"data-session=[a-zA-Z0-9]+",
});

snapshots.SaveBaseline(driver, "checkout_page");

// ... do stuff ...

snapshots.SaveCurrent(driver, "checkout_page");
DomDiffResult diff = snapshots.Diff("checkout_page");
Console.WriteLine(diff.ToReport());

// colour-coded HTML report — open in browser
string htmlPath = snapshots.SaveHtmlDiffReport("checkout_page");
```

## Ignore Patterns

No patterns are applied by default. Opt in to exactly what your app needs:

```csharp
DomSnapshotManager.IgnorePatterns.IsoTimestamps      // 2026-01-01T12:00:00
DomSnapshotManager.IgnorePatterns.ReadableTimestamps // 2026-01-01 12:00:00
DomSnapshotManager.IgnorePatterns.CspNonces          // nonce="abc123..."
DomSnapshotManager.IgnorePatterns.CacheBusters       // ?_=1234567890
DomSnapshotManager.IgnorePatterns.CsrfTokens         // csrf-token="..."
DomSnapshotManager.IgnorePatterns.SessionIds         // session-id="..."
```

You can also pass any custom regex string alongside these.

## CI Integration

```csharp
var ci = new CiArtifactHelper(snapshots, "dom_snapshots");
ci.WriteCiSummary();                    // writes ci_summary.txt, GitHub Actions annotations
ci.ExportSnapshotDirectoryToEnvironment(); // sets SELENIUM_SNAPSHOT_DIR env var
ci.PrintArtifactPaths();               // prints all file paths for CI to pick up
```

**GitHub Actions** — upload snapshots as artifacts on failure:
```yaml
- name: Upload DOM diff reports
  if: failure()
  uses: actions/upload-artifact@v3
  with:
    name: dom-diff-reports
    path: '**/dom_snapshots/**'
```

**Jenkins:**
```groovy
post { failure { archiveArtifacts artifacts: '**/dom_snapshots/**', allowEmptyArchive: true } }
```
