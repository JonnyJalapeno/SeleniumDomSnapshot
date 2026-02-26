using NUnit.Framework;
using NUnit.Framework.Interfaces;
using OpenQA.Selenium;

namespace SeleniumDomSnapshot.NUnit
{
    /// <summary>
    /// NUnit attribute that automatically captures DOM snapshots on test pass/fail.
    ///
    /// Usage:
    ///   [DomSnapshot(baselineOnPass: true)]
    ///   public void MyTest() { ... }
    ///
    /// On first passing run: saves a baseline snapshot.
    /// On failure: saves current snapshot, generates diff report if baseline exists.
    ///
    /// The attribute locates the IWebDriver instance automatically via reflection —
    /// it checks all fields and properties of the test fixture class.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public class DomSnapshotAttribute : Attribute, ITestAction
    {
        private readonly bool _baselineOnPass;
        private readonly string _snapshotDirectory;

        /// <param name="baselineOnPass">If true, saves a new baseline every time the test passes.</param>
        /// <param name="snapshotDirectory">Directory to store snapshots in.</param>
        public DomSnapshotAttribute(bool baselineOnPass = false, string snapshotDirectory = "dom_snapshots")
        {
            _baselineOnPass = baselineOnPass;
            _snapshotDirectory = snapshotDirectory;
        }

        public ActionTargets Targets => ActionTargets.Test;

        public void BeforeTest(ITest test) { }

        public void AfterTest(ITest test)
        {
            IWebDriver? driver = GetDriver(test.Fixture);
            if (driver == null) return;

            // Use FullName so baseline filenames are unique across test classes
            string testName = TestContext.CurrentContext.Test.FullName;
            var manager = new DomSnapshotManager(_snapshotDirectory);
            var status = TestContext.CurrentContext.Result.Outcome.Status;

            if (status == TestStatus.Passed && _baselineOnPass)
            {
                string path = manager.SaveBaseline(driver, testName);
                TestContext.Out.WriteLine($"[DomSnapshot] Baseline saved: {path}");
            }
            else if (status == TestStatus.Failed)
            {
                string currentPath = manager.SaveCurrent(driver, testName);
                TestContext.Out.WriteLine($"[DomSnapshot] Current snapshot saved: {currentPath}");

                if (manager.HasBaseline(testName))
                {
                    string reportPath = manager.SaveHtmlDiffReport(testName);
                    TestContext.Out.WriteLine($"[DomSnapshot] HTML diff report: {reportPath}");
                    TestContext.Out.WriteLine(manager.Diff(testName).ToReport());
                }
                else
                {
                    TestContext.Out.WriteLine("[DomSnapshot] No baseline found — run with baselineOnPass: true on a passing test first.");
                }
            }
        }

        private static IWebDriver? GetDriver(object? fixture)
        {
            if (fixture == null) return null;

            var type = fixture.GetType();

            var prop = type.GetProperties()
                .FirstOrDefault(p => typeof(IWebDriver).IsAssignableFrom(p.PropertyType));
            if (prop != null)
                return (IWebDriver?)prop.GetValue(fixture);

            var field = type.GetFields(
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Public)
                .FirstOrDefault(f => typeof(IWebDriver).IsAssignableFrom(f.FieldType));
            if (field != null)
                return (IWebDriver?)field.GetValue(fixture);

            return null;
        }
    }
}
