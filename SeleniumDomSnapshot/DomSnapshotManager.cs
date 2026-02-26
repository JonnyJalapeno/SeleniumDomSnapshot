using OpenQA.Selenium;
using HtmlAgilityPack;
using System.Text;
using System.Text.RegularExpressions;

namespace SeleniumDomSnapshot
{
    public class DomSnapshotManager
    {
        private readonly string _snapshotDirectory;
        private readonly bool _prettyPrint;
        private readonly IReadOnlyList<string> _ignorePatterns;

        /// <summary>
        /// Pre-built regex patterns you can mix and match when constructing DomSnapshotManager.
        /// No patterns are applied by default — you opt in to exactly what you need.
        /// </summary>
        public static class IgnorePatterns
        {
            public const string IsoTimestamps     = @"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}";
            public const string ReadableTimestamps = @"\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}";
            public const string CspNonces          = @"nonce=[""']?[a-zA-Z0-9+/=]{10,}[""']?";
            public const string CacheBusters       = @"_=[0-9]+";
            public const string CsrfTokens         = @"csrf[_-]?token=[""']?[a-zA-Z0-9+/=_-]+[""']?";
            public const string SessionIds         = @"session[_-]?id=[""']?[a-zA-Z0-9+/=_-]+[""']?";
        }

        public DomSnapshotManager(
            string snapshotDirectory = "dom_snapshots",
            bool prettyPrint = true,
            string[]? ignorePatterns = null)
        {
            _snapshotDirectory = snapshotDirectory;
            _prettyPrint = prettyPrint;
            _ignorePatterns = ignorePatterns ?? Array.Empty<string>();
            Directory.CreateDirectory(snapshotDirectory);
        }

        public string SaveBaseline(IWebDriver driver, string testName)
        {
            string path = GetBaselinePath(testName);
            File.WriteAllText(path, CaptureAndFormat(driver), Encoding.UTF8);
            return path;
        }

        public string SaveCurrent(IWebDriver driver, string testName)
        {
            string path = GetCurrentPath(testName);
            File.WriteAllText(path, CaptureAndFormat(driver), Encoding.UTF8);
            return path;
        }

        public DomDiffResult Diff(string testName)
        {
            string baselinePath = GetBaselinePath(testName);
            string currentPath = GetCurrentPath(testName);

            if (!File.Exists(baselinePath))
                throw new FileNotFoundException($"No baseline snapshot found for '{testName}'.", baselinePath);
            if (!File.Exists(currentPath))
                throw new FileNotFoundException($"No current snapshot found for '{testName}'.", currentPath);

            string[] baselineLines = File.ReadAllLines(baselinePath, Encoding.UTF8);
            string[] currentLines = File.ReadAllLines(currentPath, Encoding.UTF8);

            if (_ignorePatterns.Count > 0)
            {
                baselineLines = ScrubLines(baselineLines);
                currentLines = ScrubLines(currentLines);
            }

            return ComputeDiff(baselineLines, currentLines, testName);
        }

        public string SaveDiffReport(string testName)
        {
            DomDiffResult result = Diff(testName);
            string path = Path.Combine(_snapshotDirectory, $"{Sanitize(testName)}_diff_report.txt");
            File.WriteAllText(path, result.ToReport(), Encoding.UTF8);
            return path;
        }

        public string SaveHtmlDiffReport(string testName)
        {
            DomDiffResult result = Diff(testName);
            string path = Path.Combine(_snapshotDirectory, $"{Sanitize(testName)}_diff_report.html");
            File.WriteAllText(path, result.ToHtmlReport(), Encoding.UTF8);
            return path;
        }

        public bool HasBaseline(string testName) => File.Exists(GetBaselinePath(testName));

        public IEnumerable<string> GetAllSnapshotPaths() =>
            Directory.GetFiles(_snapshotDirectory, "*", SearchOption.TopDirectoryOnly);

        public IEnumerable<string> GetDiffReportPaths() =>
            Directory.GetFiles(_snapshotDirectory, "*_diff_report*", SearchOption.TopDirectoryOnly);

        private string CaptureAndFormat(IWebDriver driver)
        {
            if (!_prettyPrint)
                return (string)((IJavaScriptExecutor)driver)
                    .ExecuteScript("return document.documentElement.outerHTML");

            // The JS uses \n as a JS escape sequence inside JS string literals.
            // We do NOT pass actual newline characters — that would break JS string syntax.
            const string js = @"
var result = '';
function walk(node, depth) {
    var indent = '';
    for (var d = 0; d < depth; d++) indent += '  ';
    if (node.nodeType === 3) {
        var t = node.textContent.trim();
        if (t) result += indent + t + String.fromCharCode(10);
        return;
    }
    if (node.nodeType !== 1) return;
    var tag = node.tagName.toLowerCase();
    var attrs = '';
    for (var i = 0; i < node.attributes.length; i++) {
        attrs += ' ' + node.attributes[i].name + '=' + node.attributes[i].value;
    }
    if (tag === 'script' || tag === 'style') {
        result += indent + '<' + tag + attrs + '></' + tag + '>' + String.fromCharCode(10);
        return;
    }
    var hasChild = false;
    for (var j = 0; j < node.childNodes.length; j++) {
        if (node.childNodes[j].nodeType === 1) { hasChild = true; break; }
    }
    if (hasChild) {
        result += indent + '<' + tag + attrs + '>' + String.fromCharCode(10);
        for (var k = 0; k < node.childNodes.length; k++) walk(node.childNodes[k], depth + 1);
        result += indent + '</' + tag + '>' + String.fromCharCode(10);
    } else {
        var text = node.textContent.trim();
        result += indent + '<' + tag + attrs + '>' + text + '</' + tag + '>' + String.fromCharCode(10);
    }
}
walk(document.documentElement, 0);
return result;";

            string raw = (string)((IJavaScriptExecutor)driver).ExecuteScript(js);
            return raw ?? string.Empty;
        }

        private string[] ScrubLines(string[] lines)
        {
            return lines.Select(line =>
            {
                foreach (var pattern in _ignorePatterns)
                    line = Regex.Replace(line, pattern, "[DYNAMIC]");
                return line;
            }).ToArray();
        }

        private DomDiffResult ComputeDiff(string[] baseline, string[] current, string testName)
        {
            var added = new List<DomDiffLine>();
            var removed = new List<DomDiffLine>();
            int unchanged = 0;

            int[,] lcs = BuildLcsMatrix(baseline, current);
            var changes = TraceChanges(lcs, baseline, current, baseline.Length, current.Length);

            int lineNumber = 0;
            foreach (var change in changes)
            {
                lineNumber++;
                if (change.Type == ChangeType.Added)
                    added.Add(new DomDiffLine(lineNumber, change.Content));
                else if (change.Type == ChangeType.Removed)
                    removed.Add(new DomDiffLine(lineNumber, change.Content));
                else
                    unchanged++;
            }

            return new DomDiffResult(testName, added, removed, unchanged);
        }

        private static int[,] BuildLcsMatrix(string[] a, string[] b)
        {
            int m = a.Length;
            int n = b.Length;
            var matrix = new int[m + 1, n + 1];
            for (int i = 1; i <= m; i++)
                for (int j = 1; j <= n; j++)
                    matrix[i, j] = a[i - 1] == b[j - 1]
                        ? matrix[i - 1, j - 1] + 1
                        : Math.Max(matrix[i - 1, j], matrix[i, j - 1]);
            return matrix;
        }

        private static List<Change> TraceChanges(int[,] lcs, string[] a, string[] b, int i, int j)
        {
            if (i == 0 && j == 0) return new List<Change>();

            if (i == 0)
            {
                var r = TraceChanges(lcs, a, b, 0, j - 1);
                r.Add(new Change(ChangeType.Added, b[j - 1]));
                return r;
            }
            if (j == 0)
            {
                var r = TraceChanges(lcs, a, b, i - 1, 0);
                r.Add(new Change(ChangeType.Removed, a[i - 1]));
                return r;
            }
            if (a[i - 1] == b[j - 1])
            {
                var r = TraceChanges(lcs, a, b, i - 1, j - 1);
                r.Add(new Change(ChangeType.Unchanged, a[i - 1]));
                return r;
            }
            if (lcs[i - 1, j] >= lcs[i, j - 1])
            {
                var r = TraceChanges(lcs, a, b, i - 1, j);
                r.Add(new Change(ChangeType.Removed, a[i - 1]));
                return r;
            }
            else
            {
                var r = TraceChanges(lcs, a, b, i, j - 1);
                r.Add(new Change(ChangeType.Added, b[j - 1]));
                return r;
            }
        }

        private string GetBaselinePath(string testName) =>
            Path.Combine(_snapshotDirectory, $"{Sanitize(testName)}_baseline.html");

        private string GetCurrentPath(string testName) =>
            Path.Combine(_snapshotDirectory, $"{Sanitize(testName)}_current.html");

        private static string Sanitize(string name) =>
            string.Concat(name.Split(Path.GetInvalidFileNameChars()));
    }

    public record DomDiffLine(int LineNumber, string Content);

    public class DomDiffResult
    {
        public string TestName { get; }
        public IReadOnlyList<DomDiffLine> Added { get; }
        public IReadOnlyList<DomDiffLine> Removed { get; }
        public int UnchangedLines { get; }
        public bool HasChanges => Added.Count > 0 || Removed.Count > 0;

        public DomDiffResult(string testName, List<DomDiffLine> added, List<DomDiffLine> removed, int unchanged)
        {
            TestName = testName;
            Added = added.AsReadOnly();
            Removed = removed.AsReadOnly();
            UnchangedLines = unchanged;
        }

        public string ToReport()
        {
            var sb = new StringBuilder();
            sb.AppendLine("DOM Diff Report - " + TestName);
            sb.AppendLine("Generated: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine(new string('=', 60));
            sb.AppendLine("Unchanged lines : " + UnchangedLines);
            sb.AppendLine("Removed lines   : " + Removed.Count);
            sb.AppendLine("Added lines     : " + Added.Count);
            sb.AppendLine(new string('=', 60));

            if (!HasChanges)
            {
                sb.AppendLine("No structural changes detected.");
                return sb.ToString();
            }

            if (Removed.Count > 0)
            {
                sb.AppendLine("\nREMOVED (in baseline, missing in current):");
                sb.AppendLine(new string('-', 40));
                foreach (var line in Removed)
                    sb.AppendLine($"- [{line.LineNumber,5}] {line.Content.Trim()}");
            }

            if (Added.Count > 0)
            {
                sb.AppendLine("\nADDED (not in baseline, present in current):");
                sb.AppendLine(new string('-', 40));
                foreach (var line in Added)
                    sb.AppendLine($"+ [{line.LineNumber,5}] {line.Content.Trim()}");
            }

            return sb.ToString();
        }

        public string ToHtmlReport()
        {
            string status = HasChanges ? "&#9888; Changes Detected" : "&#9989; No Changes";
            string statusColor = HasChanges ? "#c0392b" : "#27ae60";

            // CSS is a plain string constant — no interpolation — so CSS curly braces
            // don't conflict with C# string interpolation syntax
            const string css =
                "* { box-sizing: border-box; margin: 0; padding: 0; }" +
                "body { font-family: 'Segoe UI', sans-serif; background: #1e1e2e; color: #cdd6f4; }" +
                "header { background: #181825; padding: 24px 32px; border-bottom: 1px solid #313244; }" +
                "header h1 { font-size: 1.2rem; font-weight: 600; color: #cba6f7; }" +
                "header p { font-size: 0.85rem; color: #6c7086; margin-top: 4px; }" +
                ".stats { display: flex; gap: 24px; padding: 16px 32px; background: #181825; border-bottom: 1px solid #313244; }" +
                ".stat { text-align: center; }" +
                ".stat .number { font-size: 1.5rem; font-weight: 700; }" +
                ".stat .label { font-size: 0.75rem; color: #6c7086; text-transform: uppercase; letter-spacing: 0.05em; }" +
                ".unchanged { color: #6c7086; }" +
                ".removed-num { color: #f38ba8; }" +
                ".added-num { color: #a6e3a1; }" +
                ".diff-container { padding: 24px 32px; }" +
                ".diff-container h2 { font-size: 0.9rem; font-weight: 600; text-transform: uppercase; letter-spacing: 0.1em; margin-bottom: 12px; color: #6c7086; }" +
                "table { width: 100%; border-collapse: collapse; font-family: 'Cascadia Code','Consolas',monospace; font-size: 0.8rem; margin-bottom: 32px; }" +
                "td { padding: 3px 12px; vertical-align: top; white-space: pre-wrap; word-break: break-all; }" +
                "td.line-num { width: 60px; text-align: right; color: #45475a; border-right: 1px solid #313244; user-select: none; }" +
                "td.marker { width: 24px; text-align: center; }" +
                "tr.removed { background: #3d1a1a; }" +
                "tr.removed td.marker { color: #f38ba8; }" +
                "tr.removed td.content { color: #f38ba8; }" +
                "tr.added { background: #1a3d1a; }" +
                "tr.added td.marker { color: #a6e3a1; }" +
                "tr.added td.content { color: #a6e3a1; }" +
                ".no-changes { padding: 48px; text-align: center; color: #a6e3a1; font-size: 1.1rem; }";

            var sb = new StringBuilder();
            sb.AppendLine("<!DOCTYPE html>");
            sb.AppendLine("<html lang=\"en\">");
            sb.AppendLine("<head>");
            sb.AppendLine("  <meta charset=\"UTF-8\">");
            sb.AppendLine("  <meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\">");
            sb.AppendLine($"  <title>DOM Diff - {Escape(TestName)}</title>");
            sb.AppendLine($"  <style>{css}</style>");
            // statusColor is dynamic so it goes in its own interpolated style block
            sb.AppendLine($"  <style>.status {{ display: inline-block; margin-top: 10px; padding: 4px 12px; border-radius: 20px; font-size: 0.85rem; font-weight: 600; color: {statusColor}; border: 1px solid {statusColor}; }}</style>");
            sb.AppendLine("</head>");
            sb.AppendLine("<body>");
            sb.AppendLine("  <header>");
            sb.AppendLine("    <h1>DOM Diff Report</h1>");
            sb.AppendLine($"    <p>{Escape(TestName)}</p>");
            sb.AppendLine($"    <p>Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}</p>");
            sb.AppendLine($"    <span class=\"status\">{status}</span>");
            sb.AppendLine("  </header>");
            sb.AppendLine("  <div class=\"stats\">");
            sb.AppendLine($"    <div class=\"stat\"><div class=\"number unchanged\">{UnchangedLines}</div><div class=\"label\">Unchanged</div></div>");
            sb.AppendLine($"    <div class=\"stat\"><div class=\"number removed-num\">{Removed.Count}</div><div class=\"label\">Removed</div></div>");
            sb.AppendLine($"    <div class=\"stat\"><div class=\"number added-num\">{Added.Count}</div><div class=\"label\">Added</div></div>");
            sb.AppendLine("  </div>");
            sb.AppendLine("  <div class=\"diff-container\">");

            if (!HasChanges)
            {
                sb.AppendLine("    <div class=\"no-changes\">&#9989; No structural changes detected.</div>");
            }
            else
            {
                if (Removed.Count > 0)
                {
                    sb.AppendLine("    <h2>Removed - present in baseline, missing in current</h2>");
                    sb.AppendLine("    <table>");
                    foreach (var line in Removed)
                        sb.AppendLine($"      <tr class=\"removed\"><td class=\"line-num\">{line.LineNumber}</td><td class=\"marker\">-</td><td class=\"content\">{Escape(line.Content.Trim())}</td></tr>");
                    sb.AppendLine("    </table>");
                }

                if (Added.Count > 0)
                {
                    sb.AppendLine("    <h2>Added - not in baseline, present in current</h2>");
                    sb.AppendLine("    <table>");
                    foreach (var line in Added)
                        sb.AppendLine($"      <tr class=\"added\"><td class=\"line-num\">{line.LineNumber}</td><td class=\"marker\">+</td><td class=\"content\">{Escape(line.Content.Trim())}</td></tr>");
                    sb.AppendLine("    </table>");
                }
            }

            sb.AppendLine("  </div>");
            sb.AppendLine("</body>");
            sb.AppendLine("</html>");
            return sb.ToString();
        }

        private static string Escape(string s) =>
            s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
    }

    internal record Change(ChangeType Type, string Content);
    internal enum ChangeType { Added, Removed, Unchanged }
}
