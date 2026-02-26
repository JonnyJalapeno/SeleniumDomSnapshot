using System.Text;

namespace SeleniumDomSnapshot
{
    /// <summary>
    /// Helps surface DOM diff reports as CI artifacts without requiring any specific CI platform.
    ///
    /// Works by writing a summary file that CI systems can pick up, and by printing
    /// structured output that GitHub Actions, Jenkins, and Azure DevOps all understand natively.
    ///
    /// GitHub Actions example:
    ///   - name: Upload DOM diff reports
    ///     if: failure()
    ///     uses: actions/upload-artifact@v3
    ///     with:
    ///       name: dom-diff-reports
    ///       path: '**/dom_snapshots/**'
    ///
    /// Jenkins example (Groovy pipeline):
    ///   post { failure { archiveArtifacts artifacts: '**/dom_snapshots/**', allowEmptyArchive: true } }
    ///
    /// Azure DevOps example:
    ///   - task: PublishBuildArtifacts@1
    ///     condition: failed()
    ///     inputs:
    ///       pathToPublish: 'dom_snapshots'
    ///       artifactName: 'dom-diff-reports'
    /// </summary>
    public class CiArtifactHelper
    {
        private readonly DomSnapshotManager _manager;
        private readonly string _snapshotDirectory;

        public CiArtifactHelper(DomSnapshotManager manager, string snapshotDirectory = "dom_snapshots")
        {
            _manager = manager;
            _snapshotDirectory = snapshotDirectory;
        }

        /// <summary>
        /// Writes a CI-friendly summary file listing all diff reports generated in this run.
        /// Also prints annotations that GitHub Actions renders as warnings in the build log.
        /// Call this in your test fixture teardown or CI post-step.
        /// Returns the path to the summary file.
        /// </summary>
        public string WriteCiSummary()
        {
            var reports = _manager.GetDiffReportPaths().ToList();
            string summaryPath = Path.Combine(_snapshotDirectory, "ci_summary.txt");

            var sb = new StringBuilder();
            sb.AppendLine("SeleniumDomSnapshot — CI Summary");
            sb.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine(new string('=', 60));
            sb.AppendLine($"Total diff reports generated: {reports.Count}");
            sb.AppendLine();

            if (reports.Count == 0)
            {
                sb.AppendLine("No diff reports found. Either all tests passed without changes, or no baselines exist yet.");
            }
            else
            {
                sb.AppendLine("Reports:");
                foreach (var report in reports)
                {
                    sb.AppendLine($"  {Path.GetFileName(report)}");

                    // GitHub Actions annotation — renders as a warning in the Actions UI
                    // Format: ::warning file={path}::{message}
                    PrintGitHubActionsAnnotation(report);
                }
            }

            File.WriteAllText(summaryPath, sb.ToString(), Encoding.UTF8);

            // GitHub Actions step summary — writes to the job summary page
            WriteGitHubStepSummary(reports);

            return summaryPath;
        }

        /// <summary>
        /// Prints the paths of all snapshot files so CI can locate them for artifact upload.
        /// Useful when your CI config uses a dynamic path rather than a glob.
        /// </summary>
        public void PrintArtifactPaths()
        {
            foreach (var path in _manager.GetAllSnapshotPaths())
                Console.WriteLine($"[SeleniumDomSnapshot] artifact: {Path.GetFullPath(path)}");
        }

        /// <summary>
        /// Sets the SELENIUM_SNAPSHOT_DIR environment variable so CI pipeline steps
        /// can reference the snapshot directory without hardcoding the path.
        /// </summary>
        public void ExportSnapshotDirectoryToEnvironment()
        {
            string fullPath = Path.GetFullPath(_snapshotDirectory);
            Environment.SetEnvironmentVariable("SELENIUM_SNAPSHOT_DIR", fullPath);
            Console.WriteLine($"[SeleniumDomSnapshot] SELENIUM_SNAPSHOT_DIR={fullPath}");

            // GitHub Actions — sets an output variable accessible to subsequent steps
            // Format: echo "{name}={value}" >> $GITHUB_OUTPUT
            string? githubOutput = Environment.GetEnvironmentVariable("GITHUB_OUTPUT");
            if (!string.IsNullOrEmpty(githubOutput))
                File.AppendAllText(githubOutput, $"snapshot_dir={fullPath}{Environment.NewLine}");
        }

        // -------------------------------------------------------------------------
        // Private helpers
        // -------------------------------------------------------------------------

        private static void PrintGitHubActionsAnnotation(string reportPath)
        {
            // Only emit annotations when actually running in GitHub Actions
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GITHUB_ACTIONS")))
                return;

            string fileName = Path.GetFileName(reportPath);
            Console.WriteLine($"::warning file={reportPath}::DOM diff detected — see {fileName} for details");
        }

        private static void WriteGitHubStepSummary(List<string> reports)
        {
            string? summaryFile = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
            if (string.IsNullOrEmpty(summaryFile))
                return;

            var sb = new StringBuilder();
            sb.AppendLine("## 🔍 SeleniumDomSnapshot Report");
            sb.AppendLine();

            if (reports.Count == 0)
            {
                sb.AppendLine("✅ No DOM changes detected.");
            }
            else
            {
                sb.AppendLine($"⚠️ **{reports.Count} diff report(s) generated:**");
                sb.AppendLine();
                foreach (var report in reports)
                    sb.AppendLine($"- `{Path.GetFileName(report)}`");
                sb.AppendLine();
                sb.AppendLine("Download the `dom-diff-reports` artifact to view full HTML reports.");
            }

            File.AppendAllText(summaryFile, sb.ToString());
        }
    }
}
