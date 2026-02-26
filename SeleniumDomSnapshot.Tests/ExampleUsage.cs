// ============================================================
// EXAMPLE USAGE — SeleniumDomSnapshot
// ============================================================

using NUnit.Framework;
using NUnit.Framework.Interfaces;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using SeleniumDomSnapshot;
using SeleniumDomSnapshot.NUnit;

namespace SeleniumDomSnapshot.Tests
{
    public class ExampleTests
    {
        // The [DomSnapshot] attribute finds this automatically via reflection.
        // Fields are assigned in SetUp before any test runs — null! suppresses
        // the "uninitialized" warning without lying about the actual nullability.
        protected IWebDriver driver = null!;
        private DomSnapshotManager _snapshots = null!;
        private CiArtifactHelper _ci = null!;

        [SetUp]
        public void SetUp()
        {
            driver = new ChromeDriver();

            _snapshots = new DomSnapshotManager("dom_snapshots", ignorePatterns: new[]
            {
                DomSnapshotManager.IgnorePatterns.IsoTimestamps,
                DomSnapshotManager.IgnorePatterns.ReadableTimestamps,
                DomSnapshotManager.IgnorePatterns.CspNonces,
                DomSnapshotManager.IgnorePatterns.CacheBusters,
                // add your own app-specific patterns here, for example:
                // @"data-session=[a-zA-Z0-9]+",
            });
            _ci = new CiArtifactHelper(_snapshots, "dom_snapshots");
        }

        [TearDown]
        public void TearDown()
        {
            // Must happen before Quit() — browser needs to be alive to capture.
            // Guard against SetUp having failed before driver was assigned.
            if (driver != null && TestContext.CurrentContext.Result.Outcome.Status == TestStatus.Failed)
            {
                string testName = TestContext.CurrentContext.Test.Name;
                _snapshots.SaveCurrent(driver, testName);

                if (_snapshots.HasBaseline(testName))
                {
                    string txtReport = _snapshots.SaveDiffReport(testName);
                    TestContext.WriteLine($"Text report: {txtReport}");
                    TestContext.WriteLine(_snapshots.Diff(testName).ToReport());

                    string htmlReport = _snapshots.SaveHtmlDiffReport(testName);
                    TestContext.WriteLine($"HTML report: {htmlReport}");
                }

                _ci.WriteCiSummary();
                _ci.ExportSnapshotDirectoryToEnvironment();
                _ci.PrintArtifactPaths();
            }

            driver?.Quit();
            driver?.Dispose();
        }

        private void Login()
        {
            driver.Navigate().GoToUrl("https://www.saucedemo.com");
            driver.FindElement(By.Id("user-name")).SendKeys("standard_user");
            driver.FindElement(By.Id("password")).SendKeys("secret_sauce");
            driver.FindElement(By.Id("login-button")).Click();
        }

        // -------------------------------------------------------
        // OPTION 1: Attribute — fully automatic snapshot on pass/fail
        // -------------------------------------------------------

        [Test]
        [DomSnapshot(baselineOnPass: true)]
        public void Login_WithValidCredentials_RedirectsToDashboard()
        {
            Login();
            Assert.That(driver.Url, Contains.Substring("inventory"));
        }

        // -------------------------------------------------------
        // OPTION 2: Manual with HTML report
        // -------------------------------------------------------

        [Test]
        public void AddToCart_ManualSnapshot()
        {
            Login();

            _snapshots.SaveBaseline(driver, "inventory_page_baseline");

            driver.FindElement(By.CssSelector("[data-test='add-to-cart-sauce-labs-backpack']")).Click();

            _snapshots.SaveCurrent(driver, "inventory_page_baseline");

            DomDiffResult diff = _snapshots.Diff("inventory_page_baseline");

            TestContext.WriteLine(diff.ToReport());

            string htmlPath = _snapshots.SaveHtmlDiffReport("inventory_page_baseline");
            TestContext.WriteLine($"Open in browser: {Path.GetFullPath(htmlPath)}");

            Assert.That(diff.Added.Any(l => l.Content.Contains("Remove")), Is.True);
        }

        // -------------------------------------------------------
        // OPTION 3: Verify the ignore patterns are working
        // -------------------------------------------------------

        [Test]
        public void DomSnapshot_IgnoresTimestampsAndTokens()
        {
            Login();
            _snapshots.SaveBaseline(driver, "ignore_test");

            // data-ts matches ReadableTimestamps, nonce matches CspNonces
            ((IJavaScriptExecutor)driver).ExecuteScript(@"
                var el = document.createElement('div');
                el.id = 'dynamic-test-marker';
                el.setAttribute('data-ts', '2026-01-01 12:00:00');
                el.setAttribute('nonce', 'aB3xK9mP2qzQ7rL1nW5taB3x');
                document.body.appendChild(el);
            ");

            _snapshots.SaveCurrent(driver, "ignore_test");

            DomDiffResult diff = _snapshots.Diff("ignore_test");
            TestContext.WriteLine(diff.ToReport());

            if (diff.HasChanges)
            {
                bool tokenValueLeaked = diff.Added.Any(l =>
                    l.Content.Contains("aB3xK9mP2qzQ7rL1nW5taB3x") ||
                    l.Content.Contains("2026-01-01 12:00:00"));

                Assert.That(tokenValueLeaked, Is.False,
                    "Dynamic token and timestamp values should be scrubbed to [DYNAMIC] in the diff");
            }
        }

        // -------------------------------------------------------
        // OPTION 4: Verify the diff detects real structural changes
        // -------------------------------------------------------

        [Test]
        public void DomSnapshot_DetectsStructuralChange()
        {
            Login();
            _snapshots.SaveBaseline(driver, "change_test");

            ((IJavaScriptExecutor)driver).ExecuteScript("""
                document.querySelector('[data-test="add-to-cart-sauce-labs-backpack"]')
                    .setAttribute('data-test', 'CHANGED-VALUE');
            """);

            _snapshots.SaveCurrent(driver, "change_test");

            DomDiffResult diff = _snapshots.Diff("change_test");

            TestContext.WriteLine(diff.ToReport());
            string htmlPath = _snapshots.SaveHtmlDiffReport("change_test");
            TestContext.WriteLine($"Open in browser: {Path.GetFullPath(htmlPath)}");

            Assert.That(diff.HasChanges, Is.True);
            Assert.That(diff.Removed.Any(l => l.Content.Contains("btn_inventory")), Is.True);
            Assert.That(diff.Added.Any(l => l.Content.Contains("CHANGED-VALUE")), Is.True);
        }
    }
}
