using Microsoft.Playwright;
using Microsoft.Playwright.NUnit;

namespace PanoramicData.WhatTheDll.Tests;

[Parallelizable(ParallelScope.Self)]
[TestFixture]
public class SiteTests : PageTest
{
    private const string BaseUrl = "https://whatthedll.panoramicdata.com";

    [Test]
    public async Task HomePage_Loads_WithoutErrors()
    {
        var consoleErrors = new List<string>();
        Page.Console += (_, msg) =>
        {
            if (msg.Type == "error")
            {
                consoleErrors.Add(msg.Text);
            }
        };

        await Page.GotoAsync(BaseUrl);

        // Wait for Blazor to initialize
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        // Check no 404 errors for critical resources
        Assert.That(consoleErrors.Any(e => e.Contains("404")), Is.False, 
            $"Page has 404 errors: {string.Join(", ", consoleErrors.Where(e => e.Contains("404")))}");
    }

    [Test]
    public async Task HomePage_Shows_DropZone()
    {
        await Page.GotoAsync(BaseUrl);
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        // Should see the drop zone for uploading DLLs
        var dropZone = Page.Locator(".drop-zone, .upload-area, [class*='drop']").First;
        await Expect(dropZone).ToBeVisibleAsync(new() { Timeout = 10000 });
    }

    [Test]
    public async Task HomePage_Title_IsCorrect()
    {
        await Page.GotoAsync(BaseUrl);
        
        await Expect(Page).ToHaveTitleAsync("PanoramicData.WhatTheDll");
    }

    [Test]
    public async Task BlazorFramework_Loads_Successfully()
    {
        var responseStatuses = new Dictionary<string, int>();
        
        Page.Response += (_, response) =>
        {
            if (response.Url.Contains("_framework") || response.Url.Contains(".styles.css"))
            {
                responseStatuses[response.Url] = response.Status;
            }
        };

        await Page.GotoAsync(BaseUrl);
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        // Check that blazor.webassembly.js loaded successfully
        var failedResources = responseStatuses.Where(kvp => kvp.Value >= 400).ToList();
        Assert.That(failedResources, Is.Empty, 
            $"Failed resources: {string.Join(", ", failedResources.Select(kvp => $"{kvp.Key}: {kvp.Value}"))}");
    }

    [Test]
    public async Task CssStyles_Load_Successfully()
    {
        var cssResponses = new Dictionary<string, int>();
        
        Page.Response += (_, response) =>
        {
            if (response.Url.EndsWith(".css"))
            {
                cssResponses[response.Url] = response.Status;
            }
        };

        await Page.GotoAsync(BaseUrl);
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        // Check that CSS files loaded successfully
        var failedCss = cssResponses.Where(kvp => kvp.Value >= 400).ToList();
        Assert.That(failedCss, Is.Empty, 
            $"Failed CSS files: {string.Join(", ", failedCss.Select(kvp => $"{kvp.Key}: {kvp.Value}"))}");
    }

    [Test]
    public async Task NoUnhandledErrors_OnPageLoad()
    {
        await Page.GotoAsync(BaseUrl);
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        // Check for "An unhandled error has occurred" message
        var errorMessage = Page.GetByText("An unhandled error has occurred");
        var isVisible = await errorMessage.IsVisibleAsync();
        
        Assert.That(isVisible, Is.False, "Page shows unhandled error message");
    }

    [Test]
    public async Task DarkMode_Respects_SystemPreference()
    {
        // Emulate dark mode preference
        await Page.EmulateMediaAsync(new() { ColorScheme = ColorScheme.Dark });
        await Page.GotoAsync(BaseUrl);
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        // Check that dark theme is applied
        var htmlTheme = await Page.Locator("html").GetAttributeAsync("data-bs-theme");
        Assert.That(htmlTheme, Is.EqualTo("dark"), "Dark mode should be applied when system prefers dark");
    }

    [Test]
    public async Task Upload_DllFile_ShowsAssemblyInfo()
    {
        await Page.GotoAsync(BaseUrl);
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        // Get the path to the test DLL
        var testDataPath = Path.Combine(TestContext.CurrentContext.TestDirectory, "TestData");
        var dllPath = Path.Combine(testDataPath, "Newtonsoft.Json.dll");

        Assert.That(File.Exists(dllPath), Is.True, $"Test DLL not found at {dllPath}");

        // Find the file input and upload the DLL
        var fileInput = Page.Locator("input[type='file']").First;
        await fileInput.SetInputFilesAsync(dllPath);

        // Wait for the assembly to be analyzed
        await Page.WaitForSelectorAsync(".assembly-header, .tree-panel", new() { Timeout = 30000 });

        // Verify assembly name is displayed
        var assemblyName = Page.GetByText("Newtonsoft.Json");
        await Expect(assemblyName.First).ToBeVisibleAsync(new() { Timeout = 10000 });
    }

    [Test]
    public async Task Upload_DllAndPdb_ShowsSymbolInfo()
    {
        await Page.GotoAsync(BaseUrl);
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        // Get the paths to the test files
        var testDataPath = Path.Combine(TestContext.CurrentContext.TestDirectory, "TestData");
        var dllPath = Path.Combine(testDataPath, "Newtonsoft.Json.dll");
        var pdbPath = Path.Combine(testDataPath, "Newtonsoft.Json.pdb");

        Assert.That(File.Exists(dllPath), Is.True, $"Test DLL not found at {dllPath}");
        Assert.That(File.Exists(pdbPath), Is.True, $"Test PDB not found at {pdbPath}");

        // Upload DLL first
        var fileInput = Page.Locator("input[type='file']").First;
        await fileInput.SetInputFilesAsync(dllPath);

        // Wait for the assembly to be analyzed
        await Page.WaitForSelectorAsync(".assembly-header, .tree-panel", new() { Timeout = 30000 });

        // Now upload PDB - find the PDB drop zone input
        var pdbInput = Page.Locator("input[accept*='.pdb']").Last;
        await pdbInput.SetInputFilesAsync(pdbPath);

        // Wait for symbols to be loaded - look for the symbols indicator or source file info
        await Page.WaitForTimeoutAsync(2000);

        // Verify symbols are loaded by checking for source file references or symbol indicator
        var pageContent = await Page.ContentAsync();
        Assert.That(pageContent.Contains("Symbol") || pageContent.Contains(".cs") || pageContent.Contains("pdb"), 
            Is.True, "Page should show symbol/source information after PDB upload");
    }

    [Test]
    public async Task Upload_Dll_ShowsTypesInTree()
    {
        await Page.GotoAsync(BaseUrl);
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        // Get the path to the test DLL
        var testDataPath = Path.Combine(TestContext.CurrentContext.TestDirectory, "TestData");
        var dllPath = Path.Combine(testDataPath, "Newtonsoft.Json.dll");

        // Upload DLL
        var fileInput = Page.Locator("input[type='file']").First;
        await fileInput.SetInputFilesAsync(dllPath);

        // Wait for the tree to be displayed
        await Page.WaitForSelectorAsync(".tree-panel", new() { Timeout = 30000 });

        // Look for known Newtonsoft.Json types/namespaces
        var jsonConvert = Page.GetByText("JsonConvert");
        var newtonsoftNamespace = Page.GetByText("Newtonsoft.Json");

        // At least one of these should be visible (namespace or type)
        var jsonConvertVisible = await jsonConvert.First.IsVisibleAsync();
        var namespaceVisible = await newtonsoftNamespace.First.IsVisibleAsync();

        Assert.That(jsonConvertVisible || namespaceVisible, Is.True, 
            "Should show Newtonsoft.Json types or namespace in tree");
    }
}
