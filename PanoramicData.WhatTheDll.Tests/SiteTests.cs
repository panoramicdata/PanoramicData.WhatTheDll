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
}
